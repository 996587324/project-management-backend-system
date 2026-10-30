using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Model.WorkTasks;

namespace Service;

public sealed partial class WorkTaskService
{
    private static readonly Dictionary<string, string> FileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"]="image/jpeg", [".jpeg"]="image/jpeg", [".png"]="image/png", [".webp"]="image/webp",
        [".pdf"]="application/pdf", [".txt"]="text/plain", [".csv"]="text/csv",
        [".docx"]="application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"]="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"]="application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };
    private string FilePath(string key)
    {
        var root=Path.GetFullPath(options.Value.StorageRoot);
        var path=Path.GetFullPath(Path.Combine(root, key));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("无效附件存储位置");
        return path;
    }
    public async Task<object> BeginUploadAsync(WorkActor actor, WorkUploadRequest request)
    {
        Ready(); Scope(actor, "upload");
        using var db=scope.GetConnection("calmadmin").CopyNew();
        var task=await TaskFor(db, actor, request.TaskId); await Participant(db, actor, task, "upload");
        State(task, "PendingAssessment", "InProgress", "PendingReview");
        var name=Text(request.Name, 240, "文件名称", true);
        if (name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl) || !FileTypes.ContainsKey(Path.GetExtension(name))) throw new ArgumentException("支持图片、PDF、纯文本和Office开放文档格式，文件名不能包含路径");
        if (request.Size <= 0 || request.Size > Math.Clamp(options.Value.MaxFileBytes, 1, 50 * 1024 * 1024)) throw new ArgumentException("文件为空或超过单文件限制");
        var ticket=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var row=new WorkAttachment { AppCode=actor.AppCode, TaskId=task.Id, OwnerId=actor.UserId, IssuedByKeyId=actor.KeyId, Name=name, Size=request.Size,
            StorageKey=Guid.NewGuid().ToString("N") + ".bin", TicketHash=Hash(ticket), ExpiresAtUtc=DateTime.UtcNow.AddMinutes(15) };
        var tx=await db.Ado.UseTranAsync(async () =>
        {
            await LockAsync(db, "work-file-quota:" + actor.UserId);
            var now=DateTime.UtcNow;
            var usage=await db.Queryable<WorkAttachment>().Where(x => x.OwnerId == actor.UserId && (x.Status == "Ready" || x.ExpiresAtUtc > now)).SumAsync(x => x.Size);
            if (usage + request.Size > options.Value.MaxUserBytes) throw new ArgumentException("个人附件容量已达到限制");
            await db.Insertable(row).ExecuteCommandAsync();
        });
        if (!tx.IsSuccess) throw tx.ErrorException!;
        return new { UploadId=row.Id, Ticket=ticket, UploadPath="/api/work-task-uploads/" + row.Id + "/content", row.ExpiresAtUtc };
    }
    public async Task UploadAsync(string id, string ticket, Stream stream, CancellationToken cancellationToken)
    {
        Ready(); if (ticket.Length != 64) throw new UnauthorizedAccessException("缺少上传票据");
        using var db=scope.GetConnection("calmadmin").CopyNew();
        var hash=Hash(ticket);
        var row=await db.Queryable<WorkAttachment>().FirstAsync(x => x.Id == id && x.TicketHash == hash);
        if (row == null || row.Status != "Uploading" || row.ExpiresAtUtc <= DateTime.UtcNow) throw new UnauthorizedAccessException("上传票据无效或已到期");
        if (row.IssuedByKeyId != null && !await db.Queryable<WorkPersonalKey>().AnyAsync(x => x.Id == row.IssuedByKeyId && x.RevokedAtUtc == null && x.ExpiresAtUtc > DateTime.UtcNow))
            throw new UnauthorizedAccessException("发起上传的个人凭证已撤销或到期");
        var access=await applications.GetChannelAccessAsync(row.OwnerId, row.AppCode);
        var actor=new WorkActor(access);
        var task=await TaskFor(db, actor, row.TaskId); await Participant(db, actor, task, "upload");
        State(task, "PendingAssessment", "InProgress", "PendingReview");
        var path=FilePath(row.StorageKey); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // CreateNew 防止两个并行上传相互覆盖；不将原文件名用于磁盘路径。
        try
        {
            await using var output=new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer=new byte[81920]; long total=0; int count;
            while ((count=await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total+=count;
                if (total > row.Size || total > options.Value.MaxFileBytes) throw new ArgumentException("实际文件超过声明大小");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (total != row.Size) throw new ArgumentException("文件传输不完整，请重新申请上传");
        }
        catch (IOException) when (File.Exists(path)) { throw new WorkConflictException("该上传正在处理或已有文件，请完成校验或重新申请"); }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        await db.Updateable<WorkAttachment>().SetColumns(x => new WorkAttachment { Status="Uploaded" }).Where(x => x.Id == id && x.Status == "Uploading").ExecuteCommandAsync();
    }
    public async Task<object> CompleteUploadAsync(WorkActor actor, string id)
    {
        Ready(); Scope(actor, "upload"); using var db=scope.GetConnection("calmadmin").CopyNew();
        var row=await db.Queryable<WorkAttachment>().FirstAsync(x => x.Id == id && x.AppCode == actor.AppCode && x.OwnerId == actor.UserId)
            ?? throw new UnauthorizedAccessException("上传不存在或不属于本人");
        var task=await TaskFor(db, actor, row.TaskId); await Participant(db, actor, task, "upload");
        State(task, "PendingAssessment", "InProgress", "PendingReview");
        if (row.Status == "Ready") return new { row.Id, row.Name, row.Size };
        if (row.Status != "Uploaded" || row.ExpiresAtUtc <= DateTime.UtcNow) throw new WorkConflictException("上传未完成或已到期");
        var path=FilePath(row.StorageKey);
        try
        {
            await using var file=File.OpenRead(path);
            if (file.Length != row.Size) throw new ArgumentException("文件长度校验失败");
            var bytes=new byte[16]; var length=await file.ReadAsync(bytes);
            var ext=Path.GetExtension(row.Name).ToLowerInvariant();
            var valid=ext switch
            {
                ".jpg" or ".jpeg" => length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
                ".png" => length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}),
                ".webp" => length >= 12 && Encoding.ASCII.GetString(bytes,0,4) == "RIFF" && Encoding.ASCII.GetString(bytes,8,4) == "WEBP",
                ".pdf" => length >= 5 && Encoding.ASCII.GetString(bytes,0,5) == "%PDF-",
                ".docx" or ".xlsx" or ".pptx" => ValidateOffice(file, ext),
                ".txt" or ".csv" => ValidateText(file),
                _ => false
            };
            if (!valid) throw new ArgumentException("实际文件类型与名称不符或格式不可用");
            file.Position=0; row.Sha256=Convert.ToHexString(await SHA256.HashDataAsync(file));
            row.ContentType=FileTypes[ext]; row.Status="Ready"; row.TicketHash="";
            await db.Updateable(row).ExecuteCommandAsync();
            await Audit(db, actor, task.Id, "attachment.ready", row.Id);
            return new { row.Id, row.Name, row.Size };
        }
        catch
        {
            await db.Updateable<WorkAttachment>().SetColumns(x => new WorkAttachment { Status="Rejected", TicketHash="" }).Where(x => x.Id == id).ExecuteCommandAsync();
            throw;
        }
    }
    private static bool ValidateOffice(Stream stream, string extension)
    {
        stream.Position=0;
        try
        {
            using var zip=new ZipArchive(stream, ZipArchiveMode.Read, true);
            var main=extension switch { ".docx" => "word/document.xml", ".xlsx" => "xl/workbook.xml", _ => "ppt/presentation.xml" };
            return zip.GetEntry("[Content_Types].xml") != null && zip.GetEntry(main) != null && zip.Entries.Count < 2000 &&
                zip.Entries.Sum(x => x.Length) < 100 * 1024 * 1024 && !zip.Entries.Any(x => x.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException) { return false; }
    }
    private static bool ValidateText(Stream stream)
    {
        stream.Position=0;
        try { using var reader=new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, true); return !reader.ReadToEnd().Contains('\0'); }
        catch (DecoderFallbackException) { return false; }
    }
    public async Task<(string Path, string Name, string ContentType)> DownloadAsync(WorkActor actor, string id)
    {
        Ready(); Scope(actor, "download"); using var db=scope.GetConnection("calmadmin").CopyNew();
        var row=await db.Queryable<WorkAttachment>().FirstAsync(x => x.Id == id && x.AppCode == actor.AppCode && x.Status == "Ready")
            ?? throw new UnauthorizedAccessException("附件不存在或未就绪");
        await TaskFor(db, actor, row.TaskId);
        if (row.OwnerId != actor.UserId && !await db.Queryable<WorkAttachmentLink>().AnyAsync(x => x.AttachmentId == id && x.AppCode == actor.AppCode && x.Kind == "Report"))
        {
            var submissions=await db.Queryable<WorkSubmission>().Where(x => x.TaskId == row.TaskId && x.AppCode == actor.AppCode && x.Status != "Draft" && x.Status != "Abandoned").Select(x => x.Id).ToListAsync();
            if (!await db.Queryable<WorkAttachmentLink>().AnyAsync(x => x.AttachmentId == id && submissions.Contains(x.RecordId))) throw new UnauthorizedAccessException("附件尚未正式共享到任务记录");
        }
        await Audit(db, actor, row.TaskId, "attachment.download", id);
        return (FilePath(row.StorageKey), row.Name, row.ContentType);
    }
}

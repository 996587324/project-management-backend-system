using System.Text.Json;
using Interface;
using Model.WorkTasks;
using ModelContextProtocol.Protocol;

namespace calmadmin.WorkTasks;

internal static class WorkTaskMcpTools
{
    private static readonly JsonSerializerOptions Json = WorkTaskJson.Options;
    private static object Field(string type, string description) => new { type, description };
    internal static ListToolsResult List(WorkActor actor)
    {
        var rows = new List<Tool>();
        void Add(string name, string description, string capability, Dictionary<string, object> fields, string[] required, bool readOnly=false)
        {
            if (!actor.Scopes.Split(',').Contains(capability)) return;
            rows.Add(new Tool { Name="worktask." + name, Description=description,
                InputSchema=JsonSerializer.SerializeToElement(new { type="object", properties=fields, required, additionalProperties=false }),
                Annotations=new ToolAnnotations { ReadOnlyHint=readOnly, IdempotentHint=name is not ("attachment.begin" or "attachment.upload_small"), OpenWorldHint=false } });
        }
        Dictionary<string, object> CommandFields() => new()
        {
            ["taskId"]=Field("string", "本人任务编号"), ["idempotencyKey"]=Field("string", "同次写操作使用同一唯一编号，重试不能改变内容"),
            ["expectedVersion"]=Field("integer", "先读取详情获得的任务版本"), ["text"]=Field("string", "评估、进展或成果的真实说明")
        };
        var list=new Dictionary<string, object> { ["keyword"]=Field("string","搜索标题"), ["status"]=Field("string","可选状态"), ["page"]=Field("integer","页码"), ["size"]=Field("integer","每页最多100条") };
        Add("my.list", "分页读取本人主责或参与的任务。", "read", list, [], true);
        Add("my.get", "读取本人任务要求、节点、进展和正式成果；附件正文需另有下载能力。", "read", new() { ["taskId"]=Field("string","任务编号") }, ["taskId"], true);
        var scoped=new Dictionary<string, object>(list) { ["groupId"]=Field("string","授权范围内的部门或小组编号"), ["userId"]=Field("string","授权范围内的员工编号") };
        Add("scope.list", "按当前持有人有效的工作任务授权分页查看任务、组织和人员；包括本人任务，不能超出授权范围。", "team_read", scoped, [], true);
        Add("scope.get", "按当前持有人有效授权查看任务详情和已共享成果。", "team_read", new() { ["taskId"]=Field("string","任务编号") }, ["taskId"], true);
        Add("dispatch.groups", "查询当前人员可下派任务的工作组。", "dispatch", new(), [], true);
        Add("dispatch.candidates", "按用途查询可下派的主责人、验收人或汇总负责人；查询后仍需在正式下派时重新校验。", "dispatch", new() {
            ["purpose"]=Field("string","assignee、reviewer 或 leader"), ["groupId"]=Field("string","工作组编号"), ["assigneeId"]=Field("string","查询验收或汇总负责人时提供主责人编号"),
            ["keyword"]=Field("string","可选姓名或账号"), ["page"]=Field("integer","页码"), ["size"]=Field("integer","每页最多100条") }, ["purpose","groupId"], true);
        Add("dispatch.draft", "创建或修改本人任务草稿；正式下派前须读取草稿并由派发人确认内容。", "dispatch", new() {
            ["taskId"]=Field("string","修改草稿时提供任务编号"), ["idempotencyKey"]=Field("string","写操作唯一编号，重试必须不变"), ["expectedVersion"]=Field("integer","修改草稿时提供当前版本"),
            ["title"]=Field("string","任务标题"), ["requirements"]=Field("string","任务要求"), ["acceptanceCriteria"]=Field("string","交付标准"),
            ["groupId"]=Field("string","工作组编号"), ["assigneeId"]=Field("string","主责员工编号"), ["reviewerId"]=Field("string","验收人编号，可默认派发人"),
            ["collaboratorIds"]=new { type="array", maxItems=50, items=new { type="string" }, description="同一工作组内的协作员工编号" },
            ["summaryLeaderId"]=Field("string","汇总负责人编号，可默认派发人"), ["requestedDueAt"]=Field("string","期望完成时间，ISO格式并包含时区"),
            ["requiresReview"]=Field("boolean","是否需要验收"), ["priority"]=Field("string","Low、Normal、High 或 Urgent"), ["materialRule"]=Field("string","TextOrFile 或 FileRequired") },
            ["idempotencyKey","title","groupId" ]);
        Add("dispatch.get", "读取本人创建的任务草稿或已下派任务，以便确认内容和版本。", "dispatch", new() { ["taskId"]=Field("string","任务编号") }, ["taskId"], true);
        Add("dispatch.submit", "正式下派已预览的本人草稿；只能在派发人确认本次任务的完整内容后调用，并明确传入 confirmed=true。", "dispatch", new() {
            ["taskId"]=Field("string","草稿任务编号"), ["idempotencyKey"]=Field("string","本次下派唯一编号"), ["expectedVersion"]=Field("integer","已确认的草稿版本"), ["confirmed"]=Field("boolean","派发人已确认本次任务"), ["pushOnDispatch"]=Field("boolean","是否使用已配置的微信和邮件通道通知主责员工，默认不推送") }, ["taskId","idempotencyKey","expectedVersion","confirmed"]);
        var assess=CommandFields(); assess["feasible"]=Field("boolean","是否可承担；无法承担时请说明协调事项");
        assess["startAt"]=Field("string","可选ISO开始时间，包含时区"); assess["dueAt"]=Field("string","ISO承诺完成时间，包含时区");
        assess["milestones"]=new { type="array", maxItems=30, items=new { type="object", properties=new { title=Field("string","节点名称"), dueAt=Field("string","ISO节点时间") }, required=new[] {"title","dueAt"}, additionalProperties=false } };
        Add("assessment.reply", "回复员工评估和排期；正式承诺应先向员工预览。超出要求或原承诺的延期需要主管确认。", "assess", assess, ["taskId","idempotencyKey","expectedVersion","text","feasible"]);
        var report=CommandFields(); report["progress"]=Field("integer","0至100，100不代表已验收"); report["blocked"]=Field("boolean","是否阻塞");
        report["nextStep"]=Field("string","下一步和需要支持的事项"); report["forecastDueAt"]=Field("string","最新预计完成，不改变正式承诺");
        report["clearForecast"]=Field("boolean","仅明确需要清除最新预计时间时提供true，省略其他进度字段将保留当前值");
        report["attachmentIds"]=new { type="array", maxItems=20, items=new { type="string" } };
        Add("progress.report", "向本人任务追加真实进展、阻塞和附件，不替员工验收，不生成不存在的进展。", "report", report, ["taskId","idempotencyKey","text"]);
        var draft=CommandFields(); draft["recordId"]=Field("string","更新已有草稿时提供成果编号"); draft["recordVersion"]=Field("integer","已有成果草稿版本"); draft["attachmentIds"]=new { type="array", maxItems=20, items=new { type="string" } };
        Add("deliverable.draft", "保存成果草稿并返回编号。先用my.get预览文字、附件及草稿版本，再正式提交。", "submit", draft, ["taskId","idempotencyKey","expectedVersion","text"]);
        var submit=CommandFields(); submit.Remove("text"); submit["recordId"]=Field("string","已预览的成果草稿编号"); submit["recordVersion"]=Field("integer","已预览的草稿版本");
        Add("deliverable.submit", "正式提交已预览的成果，进入任务规定的待验收或完成状态。此工具有实际业务效果，仅在员工授权本次办理后调用。", "submit", submit, ["taskId","idempotencyKey","expectedVersion","recordId","recordVersion"]);
        Add("attachment.begin", "为本人任务申请15分钟上传票据。按返回路径通过HTTPS PUT发送实际文件，并把票据置于X-Work-Upload-Ticket请求头；不能只传电脑本地路径。", "upload",
            new() { ["taskId"]=Field("string","任务编号"), ["name"]=Field("string","原文件名"), ["size"]=Field("integer","实际字节数") }, ["taskId","name","size"]);
        Add("attachment.complete", "文件传输后验证内容，得到可用于进展或成果的附件编号。", "upload", new() { ["uploadId"]=Field("string","上传编号") }, ["uploadId"]);
        Add("attachment.download", "读取有权查看的任务中已共享附件的内容；团队附件还需团队查看能力。小文件返回Base64，大文件返回凭证认证的下载路径。", "download", new() { ["attachmentId"]=Field("string","附件编号") }, ["attachmentId"], true);
        Add("attachment.upload_small", "兼容小文件上传，客户端必须读取真实文件并提供Base64，解码后最多256KB。普通文件使用begin和HTTPS上传。", "upload",
            new() { ["taskId"]=Field("string","任务编号"), ["name"]=Field("string","原文件名"), ["base64"]=Field("string","客户端读取的真实文件Base64，不能由模型编造") }, ["taskId","name","base64"]);
        Add("report.status", "断线后查询之前写操作的回执。仅返回本人仍可访问的任务结果。", "read", new() { ["operationId"]=Field("string","之前返回的操作编号") }, ["operationId"], true);
        return new ListToolsResult { Tools=rows };
    }
    internal static async Task<CallToolResult> Call(IWorkTaskService service, WorkActor actor, string name, JsonElement args, ILogger? logger = null)
    {
        async Task Audit(bool success)
        {
            try{await service.RecordMcpUseAsync(actor,name,success);}
            catch(Exception exception){logger?.LogWarning(exception,"个人MCP调用元数据记录失败，不改变已提交业务结果");}
        }
        try
        {
            var definition=List(actor).Tools.FirstOrDefault(x=>x.Name==name)??throw new UnauthorizedAccessException("此入口或凭证未开放该工具");
            if(args.ValueKind!=JsonValueKind.Object)throw new ArgumentException("工具参数必须是对象");
            foreach(var field in definition.InputSchema.GetProperty("required").EnumerateArray())
                if(!args.TryGetProperty(field.GetString()!,out var value)||value.ValueKind==JsonValueKind.Null)throw new ArgumentException("缺少必要字段："+field.GetString());
            var properties=definition.InputSchema.GetProperty("properties");
            foreach(var field in args.EnumerateObject())if(!properties.TryGetProperty(field.Name,out _))throw new ArgumentException("包含工具未声明的参数，请检查请求");
            string Value(string key) => args.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
            object result;
            switch (name)
            {
                case "worktask.my.list":
                    var query=args.Deserialize<WorkQuery>(Json) ?? new(); query.View="my";
                    result=await service.ListAsync(actor, query); break;
                case "worktask.my.get":
                    var ownDetail=await service.DetailAsync(actor, Value("taskId"));
                    var ownJson=JsonSerializer.SerializeToElement(ownDetail, Json);
                    if (ownJson.GetProperty("task").GetProperty("assigneeId").GetString()!=actor.UserId && !ownJson.GetProperty("collaborators").EnumerateArray().Any(x=>x.GetString()==actor.UserId)) throw new UnauthorizedAccessException("此工具只能读取本人主责或参与的任务");
                    result=ownDetail; break;
                case "worktask.scope.list": result=await service.OverviewAsync(actor, args.Deserialize<WorkOverviewQuery>(Json) ?? new()); break;
                case "worktask.scope.get": result=await service.DetailAsync(actor, Value("taskId")); break;
                case "worktask.dispatch.groups": result=await service.DispatchGroupsAsync(actor); break;
                case "worktask.dispatch.candidates": result=await service.CandidatesAsync(actor, args.Deserialize<WorkCandidateQuery>(Json) ?? new()); break;
                case "worktask.dispatch.draft":
                    var draftCommand=args.Deserialize<WorkTaskCommand>(Json) ?? throw new ArgumentException("缺少草稿参数");
                    draftCommand.Action="save";
                    result=await service.CommandAsync(actor, string.IsNullOrWhiteSpace(Value("taskId")) ? null : Value("taskId"), draftCommand); break;
                case "worktask.dispatch.get":
                    var dispatchDetail=await service.DetailAsync(actor, Value("taskId"));
                    if (JsonSerializer.SerializeToElement(dispatchDetail, Json).GetProperty("task").GetProperty("createdBy").GetString()!=actor.UserId) throw new UnauthorizedAccessException("此工具只能读取本人创建的任务");
                    result=dispatchDetail; break;
                case "worktask.dispatch.submit":
                    if (args.GetProperty("confirmed").ValueKind != JsonValueKind.True) throw new ArgumentException("请先确认本次任务内容");
                    var dispatchCommand=args.Deserialize<WorkTaskCommand>(Json) ?? throw new ArgumentException("缺少下派参数");
                    dispatchCommand.Action="dispatch";
                    result=await service.CommandAsync(actor, Value("taskId"), dispatchCommand); break;
                case "worktask.attachment.begin": result=await service.BeginUploadAsync(actor, args.Deserialize<WorkUploadRequest>(Json)!); break;
                case "worktask.attachment.complete": result=await service.CompleteUploadAsync(actor, Value("uploadId")); break;
                case "worktask.attachment.download":
                    var file=await service.DownloadAsync(actor,Value("attachmentId"));
                    result=new FileInfo(file.Path).Length <= 256 * 1024
                        ? new { file.Name, file.ContentType, Base64=Convert.ToBase64String(await File.ReadAllBytesAsync(file.Path)) } as object
                        : new { file.Name, file.ContentType, DownloadPath="/mcp/work-tasks/files/" + Value("attachmentId"), Authorization="Use the same personal Bearer credential in the request header" };
                    break;
                case "worktask.attachment.upload_small":
                    var base64=Value("base64");
                    if (base64.Length > 350000) throw new ArgumentException("小文件上传最多256KB");
                    var bytes=Convert.FromBase64String(base64);
                    if (bytes.Length > 256 * 1024) throw new ArgumentException("小文件上传最多256KB");
                    var upload=JsonSerializer.SerializeToElement(await service.BeginUploadAsync(actor, new() { TaskId=Value("taskId"), Name=Value("name"), Size=bytes.Length }), Json);
                    await service.UploadAsync(upload.GetProperty("uploadId").GetString()!, upload.GetProperty("ticket").GetString()!, new MemoryStream(bytes), default);
                    result=await service.CompleteUploadAsync(actor, upload.GetProperty("uploadId").GetString()!); break;
                case "worktask.report.status": result=await service.OperationAsync(actor, Value("operationId")); break;
                default:
                    var command=args.Deserialize<WorkTaskCommand>(Json) ?? throw new ArgumentException("缺少办理参数");
                    command.Action=name switch { "worktask.assessment.reply" => "assess", "worktask.progress.report" => "report", "worktask.deliverable.draft" => "submission.save", "worktask.deliverable.submit" => "submit", _ => throw new ArgumentException("未知任务工具") };
                    result=await service.CommandAsync(actor, Value("taskId"), command); break;
            }
            var element=JsonSerializer.SerializeToElement(result, Json);
            await Audit(true);
            return new CallToolResult { Content=[new TextContentBlock { Text=element.GetRawText() }], StructuredContent=element, IsError=false };
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException or WorkConflictException or InvalidOperationException or FormatException or JsonException)
        {
            await Audit(false);
            return new CallToolResult { IsError=true, Content=[new TextContentBlock { Text=JsonSerializer.Serialize(new { error=exception.Message }, Json) }] };
        }
    }
}

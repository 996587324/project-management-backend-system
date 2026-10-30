using System.Text.Json;
using Model.WorkTasks;
using SqlSugar;

namespace Service;

public sealed partial class WorkTaskService
{
    public async Task<WorkCommandResult> CommandAsync(WorkActor actor, string? id, WorkTaskCommand command)
    {
        Ready();
        command.Milestones ??= [];
        var capability = command.Action switch { "assess" => "assess", "report" or "comment" => "report", "submission.save" or "submit" => "submit", "save" or "dispatch" => "dispatch", _ => "manage" };
        Scope(actor, capability);
        Text(command.IdempotencyKey, 128, "操作幂等标识", true);
        var receiptId = Hash($"{actor.AppCode}:{actor.UserId}:{command.Action}:{command.IdempotencyKey}");
        var payloadHash = Hash(JsonSerializer.Serialize(new { id, command }, Json));
        using var db = scope.GetConnection("calmadmin").CopyNew();
        WorkCommandResult? answer = null;
        var transaction = await db.Ado.UseTranAsync(async () =>
        {
            await LockAsync(db, "work-command:" + receiptId);
            var old = await db.Queryable<WorkReceipt>().FirstAsync(x => x.Id == receiptId);
            if (old != null)
            {
                if (old.PayloadHash != payloadHash) throw new WorkConflictException("同一操作标识不能用于不同内容");
                await TaskFor(db, actor, old.TaskId);
                answer = JsonSerializer.Deserialize<WorkCommandResult>(old.ResultJson, Json)!;
                return;
            }
            var isNew = string.IsNullOrEmpty(id);
            if (isNew && command.Action != "save") throw new ArgumentException("请先保存任务草稿");
            var taskId = id ?? Guid.NewGuid().ToString("N");
            await LockAsync(db, "work-task:" + taskId);
            var task = isNew ? new WorkTask { Id=taskId, AppCode=actor.AppCode, CreatedBy=actor.UserId, DispatcherId=actor.UserId } : await TaskFor(db, actor, taskId);
            if (!isNew && command.Action is not ("report" or "comment") && task.Version != command.ExpectedVersion)
                throw new WorkConflictException("任务已被修改，请刷新后再处理");
            var recordId = task.Id;
            var emits = true;
            switch (command.Action)
            {
                case "save":
                    State(task, "Draft");
                    if (task.CreatedBy != actor.UserId) throw new UnauthorizedAccessException("只能编辑本人草稿");
                    ApplyRequirements(task, command, false);
                    await Dispatcher(db, actor, task);
                    if (command.CollaboratorIds != null) await SaveParticipants(db, actor, task, command.CollaboratorIds);
                    emits = false;
                    break;
                case "participants.change":
                    State(task, "PendingAssessment", "InProgress", "PendingReview"); await Dispatcher(db, actor, task);
                    if (command.CollaboratorIds == null) throw new ArgumentException("请提供协作人员名单");
                    await SaveParticipants(db, actor, task, command.CollaboratorIds);
                    await ValidateAssignment(db, actor, task);
                    emits = false;
                    break;
                case "dispatch":
                    State(task, "Draft"); await Dispatcher(db, actor, task);
                    if (task.CreatedBy != actor.UserId) throw new UnauthorizedAccessException("只能下派本人草稿");
                    Text(task.Title,160,"任务标题",true); Text(task.Requirements,20000,"任务要求",true); Text(task.AcceptanceCriteria,10000,"交付标准",true);
                    await SaveParticipants(db, actor, task, await db.Queryable<WorkTaskParticipant>().Where(x=>x.AppCode==task.AppCode && x.TaskId==task.Id && x.Enabled).Select(x=>x.UserId).ToListAsync());
                    await ValidateAssignment(db, actor, task);
                    if (command.PushOnDispatch)
                    {
                        var rule = await db.Queryable<WorkNotificationRule>().FirstAsync(x => x.AppCode == actor.AppCode);
                        if (rule == null || !rule.Channels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(x => x is "Email" or "WeChat"))
                            throw new ArgumentException("请先在工作设置中配置邮件或微信推送通道");
                    }
                    task.DispatcherId = actor.UserId; task.Status = "PendingAssessment";
                    await Revision(db, actor, task);
                    break;
                case "revise":
                    State(task, "PendingAssessment", "InProgress"); await Dispatcher(db, actor, task);
                    if (command.AssigneeId != task.AssigneeId || command.GroupId != task.GroupId)
                        throw new ArgumentException("已下派任务不能通过变更要求更换主责员工或工作组，请使用转派办理责任交接");
                    Text(command.Text, 2000, "变更原因", true);
                    ApplyRequirements(task, command); await Dispatcher(db, actor, task);
                    await ValidateAssignment(db, actor, task);
                    task.Status = "PendingAssessment"; task.PlanId = null;
                    await Revision(db, actor, task);
                    break;
                case "reassign":
                    State(task, "PendingAssessment", "InProgress", "PendingReview"); await Dispatcher(db, actor, task);
                    Text(command.Text, 2000, "转派原因", true);
                    task.AssigneeId = Text(command.AssigneeId, 64, "主责人", true);
                    await db.Updateable<WorkTaskParticipant>().SetColumns(x => x.Enabled == false).Where(x => x.AppCode == task.AppCode && x.TaskId == task.Id && x.UserId == task.AssigneeId && x.Enabled).ExecuteCommandAsync();
                    if (!string.IsNullOrWhiteSpace(command.ReviewerId)) task.ReviewerId = command.ReviewerId;
                    if (!string.IsNullOrWhiteSpace(command.SummaryLeaderId)) task.SummaryLeaderId = command.SummaryLeaderId;
                    await Dispatcher(db, actor, task); await ValidateAssignment(db, actor, task);
                    task.Status = "PendingAssessment"; task.PlanId = null; task.CommittedDueAtUtc = null;
                    task.OriginalDueAtUtc = null; task.SubmittedAtUtc = null; task.Blocked = false; task.Progress = 0;
                    await db.Updateable<WorkSubmission>().SetColumns(x => new WorkSubmission { Status="Superseded" })
                        .Where(x => x.TaskId == task.Id && x.Status == "Submitted").ExecuteCommandAsync();
                    await db.Updateable<WorkSubmission>().SetColumns(x => new WorkSubmission { Status="Abandoned" })
                        .Where(x => x.TaskId == task.Id && x.Status == "Draft").ExecuteCommandAsync();
                    await Revision(db, actor, task);
                    break;
                case "assess":
                    State(task, "PendingAssessment", "InProgress"); await Worker(db, actor, task, "assess");
                    var plan = new WorkPlan { AppCode=actor.AppCode, TaskId=task.Id, RevisionId=task.RevisionId,
                        ProposedBy=actor.UserId, Explanation=Text(command.Text, 10000, "评估说明", true),
                        StartAtUtc=command.StartAt?.UtcDateTime, DueAtUtc=command.DueAt?.UtcDateTime };
                    if (command.Feasible)
                    {
                        if (plan.DueAtUtc == null || (plan.StartAtUtc.HasValue && plan.StartAtUtc > plan.DueAtUtc)) throw new ArgumentException("请填写有效的预计完成时间和开始时间");
                        plan.Status = (task.RequestedDueAtUtc.HasValue && plan.DueAtUtc > task.RequestedDueAtUtc) ||
                            (task.CommittedDueAtUtc.HasValue && plan.DueAtUtc > task.CommittedDueAtUtc) ? "Pending" : "Approved";
                        if (plan.Status == "Approved") { plan.DecidedBy=actor.UserId; plan.DecidedAtUtc=DateTime.UtcNow; ActivatePlan(task, plan); }
                    }
                    else { plan.Status="NeedsCoordination"; plan.DueAtUtc=null; }
                    if (command.Milestones.Count > 30) throw new ArgumentException("最多设置30个时间节点");
                    await db.Updateable<WorkPlan>().SetColumns(x => new WorkPlan { Status="Superseded" })
                        .Where(x => x.TaskId == task.Id && x.Status == "Pending").ExecuteCommandAsync();
                    await db.Insertable(plan).ExecuteCommandAsync();
                    foreach (var (node, sort) in command.Milestones.Select((node, sort) => (node, sort)))
                    {
                        if (plan.DueAtUtc == null || node.DueAt.UtcDateTime > plan.DueAtUtc ||
                            (plan.StartAtUtc.HasValue && node.DueAt.UtcDateTime < plan.StartAtUtc)) throw new ArgumentException("节点时间须处于本次排期范围内");
                        await db.Insertable(new WorkMilestone { AppCode=actor.AppCode, TaskId=task.Id, PlanId=plan.Id,
                            Title=Text(node.Title, 160, "节点名称", true), DueAtUtc=node.DueAt.UtcDateTime, Sort=sort }).ExecuteCommandAsync();
                    }
                    recordId = plan.Id;
                    break;
                case "plan.approve": case "plan.reject":
                    State(task, "PendingAssessment", "InProgress"); await Dispatcher(db, actor, task);
                    var pending = await db.Queryable<WorkPlan>().FirstAsync(x => x.Id == command.RecordId && x.TaskId == task.Id && x.AppCode == actor.AppCode)
                        ?? throw new ArgumentException("排期不存在");
                    if (pending.Status != "Pending" || pending.RevisionId != task.RevisionId || pending.ProposedBy != task.AssigneeId) throw new WorkConflictException("排期或责任人已变化，请重新评估");
                    pending.Status = command.Action == "plan.approve" ? "Approved" : "Rejected";
                    pending.DecidedBy=actor.UserId; pending.DecidedAtUtc=DateTime.UtcNow;
                    if (command.Action == "plan.reject") Text(command.Text, 2000, "拒绝原因", true);
                    if (pending.Status == "Approved") ActivatePlan(task, pending);
                    await db.Updateable(pending).ExecuteCommandAsync(); recordId=pending.Id;
                    break;
                case "report": case "comment":
                    State(task, "PendingAssessment", "InProgress", "PendingReview");
                    if (command.Action == "report") await Participant(db, actor, task, "report");
                    var report = new WorkReport { AppCode=actor.AppCode, TaskId=task.Id, ActorId=actor.UserId,
                        Kind=command.Action == "comment" ? "Comment" : "Progress", Text=Text(command.Text, 10000, "说明", true),
                        NextStep=Text(command.NextStep, 4000, "下一步"), Progress=Math.Clamp(command.Progress??task.Progress, 0, 100), Blocked=command.Blocked??task.Blocked, Source=actor.Source };
                    await db.Insertable(report).ExecuteCommandAsync();
                    await Link(db, actor, task, report.Id, "Report", command.AttachmentIds??[]);
                    if (command.Action == "report" && actor.UserId == task.AssigneeId) { task.Progress=report.Progress; task.Blocked=report.Blocked;
                        if(command.ClearForecast)task.ForecastDueAtUtc=null;else if(command.ForecastDueAt.HasValue)task.ForecastDueAtUtc=command.ForecastDueAt.Value.UtcDateTime; }
                    recordId=report.Id; emits=false;
                    break;
                case "submission.save":
                    State(task, "InProgress"); await Worker(db, actor, task, "submit");
                    WorkSubmission submission;
                    if (string.IsNullOrEmpty(command.RecordId))
                    {
                        submission = new WorkSubmission { AppCode=actor.AppCode, TaskId=task.Id, ActorId=actor.UserId, RevisionId=task.RevisionId, PlanId=task.PlanId };
                        await db.Insertable(submission).ExecuteCommandAsync();
                    }
                    else
                    {
                        submission = await OwnDraft(db, actor, task, command.RecordId);
                        if (submission.Version != command.RecordVersion) throw new WorkConflictException("成果草稿已变化，请刷新");
                        submission.Version++;
                        if(command.AttachmentIds!=null)await db.Deleteable<WorkAttachmentLink>().Where(x => x.RecordId == submission.Id && x.AppCode == actor.AppCode).ExecuteCommandAsync();
                    }
                    submission.Text=Text(command.Text, 10000, "成果说明");
                    submission.RevisionId=task.RevisionId; submission.PlanId=task.PlanId;
                    await Link(db, actor, task, submission.Id, "Submission", command.AttachmentIds??[]);
                    await db.Updateable(submission).ExecuteCommandAsync(); recordId=submission.Id; emits=false;
                    break;
                case "submit":
                    State(task, "InProgress"); await Worker(db, actor, task, "submit");
                    var draft = await OwnDraft(db, actor, task, command.RecordId ?? "");
                    if (draft.Version != command.RecordVersion || draft.RevisionId != task.RevisionId || draft.PlanId != task.PlanId) throw new WorkConflictException("成果依据或版本已变化，请重新保存并预览");
                    var files = await db.Queryable<WorkAttachmentLink>().Where(x => x.RecordId == draft.Id && x.AppCode == actor.AppCode).ToListAsync();
                    if ((task.MaterialRule == "FileRequired" && files.Count == 0) || (string.IsNullOrWhiteSpace(draft.Text) && files.Count == 0)) throw new ArgumentException("请按交付要求填写成果说明或上传材料");
                    foreach (var file in files)
                        if (!await db.Queryable<WorkAttachment>().AnyAsync(x => x.Id == file.AttachmentId && x.TaskId == task.Id && x.Status == "Ready")) throw new ArgumentException("有附件尚不可用");
                    await ValidateAssignment(db, actor, task);
                    draft.Status="Submitted"; draft.SubmittedAtUtc=DateTime.UtcNow;
                    await db.Updateable(draft).ExecuteCommandAsync();
                    task.SubmittedAtUtc=draft.SubmittedAtUtc; task.Status=task.RequiresReview ? "PendingReview" : "Completed";
                    if (!task.RequiresReview) { task.CompletedAtUtc=DateTime.UtcNow; task.Progress=100; }
                    recordId=draft.Id;
                    break;
                case "review.pass": case "review.return":
                    State(task, "PendingReview"); await Dispatcher(db, actor, task, true);
                    if (task.ReviewerId != actor.UserId || task.AssigneeId == actor.UserId) throw new UnauthorizedAccessException("仅指定验收人可以处理，且不能自验收");
                    var submitted = await db.Queryable<WorkSubmission>().FirstAsync(x => x.Id == command.RecordId && x.TaskId == task.Id && x.AppCode == actor.AppCode && x.Status == "Submitted")
                        ?? throw new WorkConflictException("本次提交已处理或不存在");
                    var review = new WorkReview { AppCode=actor.AppCode, TaskId=task.Id, SubmissionId=submitted.Id, ActorId=actor.UserId,
                        Accepted=command.Action == "review.pass", Opinion=Text(command.Text, 4000, "验收意见", true) };
                    await db.Insertable(review).ExecuteCommandAsync();
                    submitted.Status=review.Accepted ? "Accepted" : "Returned"; await db.Updateable(submitted).ExecuteCommandAsync();
                    task.Status=review.Accepted ? "Completed" : "InProgress";
                    if (review.Accepted) { task.CompletedAtUtc=DateTime.UtcNow; task.Progress=100; task.Blocked=false; }
                    recordId=review.Id;
                    break;
                case "cancel":
                    State(task, "Draft", "PendingAssessment", "InProgress", "PendingReview"); await Dispatcher(db, actor, task);
                    if(task.Status == "Draft") emits=false;
                    Text(command.Text, 2000, "取消原因", true); task.Status="Cancelled";
                    break;
                default: throw new ArgumentException("未开放的工作任务操作");
            }
            if (!isNew && command.Action is not ("report" or "comment" or "submission.save")) task.Version++;
            task.UpdatedAtUtc=DateTime.UtcNow;
            if (isNew) await db.Insertable(task).ExecuteCommandAsync(); else await db.Updateable(task).ExecuteCommandAsync();
            await Audit(db, actor, task.Id, command.Action, command.Action == "submission.save" ? "成果草稿 " + recordId : Text(command.Text, 10000, "操作说明"));
            if (emits)
                await db.Insertable(new WorkEvent { AppCode=actor.AppCode, TaskId=task.Id, Action=command.Action,
                    PushRequested=command.Action == "dispatch" && command.PushOnDispatch, SnapshotJson=JsonSerializer.Serialize(task, Json) }).ExecuteCommandAsync();
            answer = new WorkCommandResult(task.Id, task.Version, recordId, receiptId);
            await db.Insertable(new WorkReceipt { Id=receiptId, AppCode=actor.AppCode, OwnerId=actor.UserId,
                TaskId=task.Id, PayloadHash=payloadHash, ResultJson=JsonSerializer.Serialize(answer, Json) }).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException ?? new InvalidOperationException("任务保存失败");
        return answer!;
    }
    private static void ApplyRequirements(WorkTask task, WorkTaskCommand command, bool strict = true)
    {
        task.Title=Text(command.Title, 160, "任务标题", true);
        task.Requirements=Text(command.Requirements, 20000, "任务要求", strict);
        task.AcceptanceCriteria=Text(command.AcceptanceCriteria, 10000, "交付标准", strict);
        task.GroupId=Text(command.GroupId, 64, "工作组", true);
        task.AssigneeId=Text(command.AssigneeId, 64, "主责人", strict);
        task.ReviewerId=string.IsNullOrEmpty(command.ReviewerId) ? task.DispatcherId : Text(command.ReviewerId, 64, "验收人");
        task.SummaryLeaderId=string.IsNullOrEmpty(command.SummaryLeaderId) ? task.DispatcherId : Text(command.SummaryLeaderId, 64, "汇总负责人");
        if (command.Priority is not ("Low" or "Normal" or "High" or "Urgent") || command.MaterialRule is not ("TextOrFile" or "FileRequired")) throw new ArgumentException("无效优先级或成果规则");
        task.Priority=command.Priority; task.MaterialRule=command.MaterialRule;
        task.RequestedDueAtUtc=command.RequestedDueAt?.UtcDateTime; task.RequiresReview=command.RequiresReview;
    }
    private static async Task SaveParticipants(SqlSugar.ISqlSugarClient db, WorkActor actor, WorkTask task, List<string> ids)
    {
        if (ids.Count > 50 || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct().Count() != ids.Count || ids.Contains(task.AssigneeId)) throw new ArgumentException("协作人不能重复或包含主责人，最多50人");
        if (task.RequiresReview && ids.Contains(task.ReviewerId)) throw new ArgumentException("验收人不能是任务参与员工");
        foreach (var id in ids)
        {
            await ValidatePerson(db, actor.AppCode, id);
            if (!await db.Queryable<WorkMember>().AnyAsync(x => x.AppCode == actor.AppCode && x.GroupId == task.GroupId && x.UserId == id && x.Enabled) || !await Has(db, actor, ["dispatcher"], task.GroupId, id)) throw new UnauthorizedAccessException("协作人不在可下派的工作组或授权范围内");
            if (!await Has(db, new WorkActor(new Model.SystemApplications.ApplicationAccessContext { AppCode=actor.AppCode, UserId=id }), ["worker"], task.GroupId, id)) throw new ArgumentException("协作人尚未获得执行员工授权");
        }
        var existing = await db.Queryable<WorkTaskParticipant>().Where(x => x.AppCode == actor.AppCode && x.TaskId == task.Id).ToListAsync();
        var before = existing.Where(x=>x.Enabled).Select(x=>x.UserId).OrderBy(x=>x).ToArray();
        foreach (var row in existing) { row.Enabled=ids.Contains(row.UserId); await db.Updateable(row).ExecuteCommandAsync(); }
        foreach (var id in ids.Where(id => !existing.Any(x => x.UserId == id))) await db.Insertable(new WorkTaskParticipant { AppCode=actor.AppCode, TaskId=task.Id, UserId=id }).ExecuteCommandAsync();
        if (!before.SequenceEqual(ids.OrderBy(x=>x))) await Audit(db, actor, task.Id, "participants.change", JsonSerializer.Serialize(new { Before=before, After=ids.OrderBy(x=>x).ToArray() },Json));
    }
    private static async Task Revision(ISqlSugarClient db, WorkActor actor, WorkTask task)
    {
        var revision = new WorkRevision { AppCode=actor.AppCode, TaskId=task.Id, ActorId=actor.UserId, Version=task.Version + 1 };
        task.RevisionId=revision.Id;
        revision.SnapshotJson=JsonSerializer.Serialize(task, Json);
        await db.Insertable(revision).ExecuteCommandAsync();
    }
    private static void ActivatePlan(WorkTask task, WorkPlan plan)
    {
        task.PlanId=plan.Id; task.CommittedDueAtUtc=plan.DueAtUtc;
        task.OriginalDueAtUtc ??= plan.DueAtUtc; task.Status="InProgress";
    }
    private static async Task<WorkSubmission> OwnDraft(ISqlSugarClient db, WorkActor actor, WorkTask task, string id) =>
        await db.Queryable<WorkSubmission>().FirstAsync(x => x.Id == id && x.TaskId == task.Id && x.AppCode == actor.AppCode && x.ActorId == actor.UserId && x.Status == "Draft")
        ?? throw new UnauthorizedAccessException("成果草稿不存在或不属于当前主责人");
    private static async Task Link(ISqlSugarClient db, WorkActor actor, WorkTask task, string recordId, string kind, List<string> ids)
    {
        if (ids.Count > 20 || ids.Distinct().Count() != ids.Count) throw new ArgumentException("附件最多20个且不能重复");
        foreach (var id in ids)
        {
            await LockAsync(db,"work-file:"+id);
            var owned = await db.Queryable<WorkAttachment>().AnyAsync(x => x.Id == id && x.TaskId == task.Id && x.AppCode == actor.AppCode && x.OwnerId == actor.UserId && x.Status == "Ready");
            var shared = kind == "Submission" && task.AssigneeId == actor.UserId && await db.Queryable<WorkAttachment>().AnyAsync(x => x.Id == id && x.TaskId == task.Id && x.AppCode == actor.AppCode && x.Status == "Ready") && await db.Queryable<WorkAttachmentLink>().AnyAsync(x => x.AppCode == actor.AppCode && x.TaskId == task.Id && x.AttachmentId == id && x.Kind == "Report");
            if (!owned && !shared)
                throw new UnauthorizedAccessException("附件未就绪或不属于本次办理人和任务");
            await db.Insertable(new WorkAttachmentLink { AppCode=actor.AppCode, TaskId=task.Id, RecordId=recordId, Kind=kind, AttachmentId=id }).ExecuteCommandAsync();
        }
    }
}

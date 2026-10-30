using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using calmadmin.Config;
using calmadmin.Controllers;
using calmadmin.WorkTasks;
using Interface;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Model.ExternalTools;
using Model.EmailPush;
using Model.WeChatClawBot;
using Model.SystemApplications;
using Model.User;
using Model.WorkTasks;
using ModelContextProtocol.Protocol;
using Service;
using SqlSugar;

var database="CodexWorkTask_" + Guid.NewGuid().ToString("N");
var connection=Environment.GetEnvironmentVariable("WORK_TASK_SMOKE_SQL") ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false;Connect Timeout=15";
await using var master=new SqlConnection(new SqlConnectionStringBuilder(connection) { InitialCatalog="master" }.ConnectionString);
await master.OpenAsync(); await new SqlCommand($"CREATE DATABASE [{database}]",master).ExecuteNonQueryAsync();
var storage=Path.Combine(Path.GetTempPath(),database);
SqlSugarScope? scope=null; WebApplication? app=null; var passed=0;
var holdForUi=Environment.GetEnvironmentVariable("WORK_TASK_UI_HOLD")=="1";
var stopUi=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
try
{
    scope=new SqlSugarScope(new ConnectionConfig { ConfigId="calmadmin", DbType=SqlSugar.DbType.SqlServer, InitKeyType=InitKeyType.Attribute, IsAutoCloseConnection=true,
        ConnectionString=new SqlConnectionStringBuilder(connection) { InitialCatalog=database }.ConnectionString });
    var db=scope.GetConnectionScope("calmadmin");
    await new SystemDatabaseInitializer(scope,new LoggerFactory().CreateLogger<SystemDatabaseInitializer>()).InitializeAsync(default);
    var initializer=new WorkTaskInitializer(scope); await initializer.InitializeAsync(default);
    await new WorkTaskInitializer(scope).InitializeAsync(default);
    Check(db.DbMaintenance.IsAnyTable("work_task_revision",false),"SQL: new schema and repeat initialization");
    await db.Insertable(new WorkPlan { TaskId="migration-fixture", AppCode="A", ProposedBy="employee" }).ExecuteCommandAsync();
    await db.Ado.ExecuteCommandAsync("ALTER TABLE [work_task_plan] DROP COLUMN [DecidedAtUtc]");
    await new WorkTaskInitializer(scope).InitializeAsync(default);
    Check(await db.Ado.GetIntAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='work_task_plan' AND COLUMN_NAME='DecidedAtUtc' AND IS_NULLABLE='YES'") == 1,
        "SQL: existing table migration restores nullable column");
    Check((await db.Queryable<WorkPlan>().FirstAsync(x=>x.TaskId=="migration-fixture")).DecidedAtUtc == null,"SQL: migration preserves old row and genuine null timestamps");
    var nullableContext=new NullabilityInfoContext();var checkedNullable=0;
    foreach(var entity in typeof(WorkTask).Assembly.GetTypes().Where(x=>x.Namespace=="Model.WorkTasks"&&x.GetCustomAttributesData().Any(a=>a.AttributeType.Name=="SugarTable")))
    {
        var table=entity.GetCustomAttributesData().First(x=>x.AttributeType.Name=="SugarTable").ConstructorArguments[0].Value!.ToString()!;
        foreach(var property in entity.GetProperties().Where(x=>Nullable.GetUnderlyingType(x.PropertyType)!=null||x.PropertyType==typeof(string)&&nullableContext.Create(x).ReadState==NullabilityState.Nullable))
        {
            var column=property.GetCustomAttributesData().FirstOrDefault(x=>x.AttributeType.Name=="SugarColumn");
            if(column==null||!column.NamedArguments.Any(x=>x.MemberName=="IsNullable"&&x.TypedValue.Value is true))throw new Exception("Missing explicit IsNullable: "+table+"."+property.Name);
            if(await db.Ado.GetIntAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME=@t AND COLUMN_NAME=@c AND IS_NULLABLE='YES'",new{t=table,c=property.Name})!=1)throw new Exception("SQL column rejects null: "+table+"."+property.Name);
            checkedNullable++;
        }
    }
    Check(checkedNullable>20,"SQL: all optional entity columns explicitly declare and actually permit NULL");
    foreach(var id in new[]{"lead","peer-lead","employee","other","mixed","observer"})
    {
        await db.Insertable(new SysUserInfo { UserId=id, UserName=id, NickName=id, Password="test-only-no-login", Enabled=true, Roles="R_USER", CreateTime=DateTime.Now }).ExecuteCommandAsync();
        foreach(var appCode in new[]{"A","B"}) await db.Insertable(new SysUserApplicationEntity { UserId=id, AppCode=appCode, Enabled=true, PermissionsJson="[\"*\"]", McpScopesJson="[]", UpdateTime=DateTime.Now }).ExecuteCommandAsync();
    }
    var applications=DispatchProxy.Create<ISystemApplicationService, ApplicationProbe>();
    ApplicationProbe.Resolve=async (user,code)=>
    {
        if(!await db.Queryable<SysUserInfo>().AnyAsync(x=>x.UserId==user&&x.Enabled) || !await db.Queryable<SysUserApplicationEntity>().AnyAsync(x=>x.UserId==user&&x.AppCode==code&&x.Enabled)) throw new UnauthorizedAccessException("revoked");
        return Access(user,code);
    };
    var service=new WorkTaskService(scope,initializer,applications,Options.Create(new WorkTaskOptions { StorageRoot=storage }));
    var admin=new WorkActor(Access("1")); var lead=new WorkActor(Access("lead")); var employee=new WorkActor(Access("employee")); var other=new WorkActor(Access("other"));
    var group=J(await service.SaveGroupAsync(admin,new WorkGroup { Name="生产组" })).GetProperty("id").GetString()!;
    var repair=J(await service.SaveGroupAsync(admin,new WorkGroup { Name="维修组" })).GetProperty("id").GetString()!;
    foreach(var user in new[]{"lead","peer-lead","employee","other","mixed"}) await service.SaveMemberAsync(admin,new WorkMember { GroupId=group, UserId=user });
    await service.SaveGrantAsync(admin,new WorkGrant { UserId="lead", Role="dispatcher", Scope="Group", TargetId=group });
    await service.SaveGrantAsync(admin,new WorkGrant { UserId="peer-lead", Role="dispatcher", Scope="Group", TargetId=group });
    var reviewer=new WorkActor(Access("peer-lead"));
    await service.SaveGrantAsync(admin,new WorkGrant { UserId="observer", Role="supervisor", Scope="Group", TargetId=group });
    foreach(var user in new[]{"employee","other","mixed"}) await service.SaveGrantAsync(admin,new WorkGrant { UserId=user,Role="worker",Scope="Self" });
    await service.SaveGrantAsync(admin,new WorkGrant { UserId="mixed",Role="dispatcher",Scope="Group",TargetId=repair });
    Check(J(await service.BootstrapAsync(employee)).GetProperty("targets").GetArrayLength()==0,"scope: employee picker does not enumerate the team");
    await Reject(()=>service.CandidatesAsync(employee,new(){GroupId=group}),"candidates: employee cannot enumerate assignment candidates");
    var candidatePage=J(await service.CandidatesAsync(lead,new(){GroupId=group,Size=1}));
    var nextCandidatePage=J(await service.CandidatesAsync(lead,new(){GroupId=group,Size=1,Page=2}));
    Check(candidatePage.GetProperty("total").GetInt32()==5&&candidatePage.GetProperty("records")[0].GetProperty("value").GetString()!=nextCandidatePage.GetProperty("records")[0].GetProperty("value").GetString(),"candidates: stable pagination preserves full authorized count");
    var matching=J(await service.CandidatesAsync(lead,new(){GroupId=group,Keyword="employee",SelectedId="lead"}));
    Check(matching.GetProperty("total").GetInt32()==1&&matching.GetProperty("selected").GetProperty("disabled").GetBoolean()&&matching.GetProperty("selected").GetProperty("reason").GetString()!.Contains("执行"),"candidates: search and selected resolution explain missing execution grant");
    var leadership=J(await service.CandidatesAsync(lead,new(){Purpose="leader",GroupId=group,AssigneeId="employee"}));
    Check(leadership.GetProperty("records").EnumerateArray().Any(x=>x.GetProperty("value").GetString()=="observer"),"candidates: eligible manager need not be a member of the assigned work group");
    var reviewers=J(await service.CandidatesAsync(lead,new(){Purpose="reviewer",GroupId=group,AssigneeId="lead"}));
    Check(reviewers.GetProperty("records").EnumerateArray().Single(x=>x.GetProperty("value").GetString()=="lead").GetProperty("disabled").GetBoolean(),"candidates: self-review selection is disabled before save");
    await Reject(()=>service.CandidatesAsync(lead,new(){GroupId=repair}),"candidates: group query cannot cross dispatcher scope");
    var draft=new WorkTaskCommand { Action="save", Title="检查设备", Requirements="确认异常并处理", AcceptanceCriteria="说明原因及处理结果", GroupId=group, AssigneeId="employee", ReviewerId="peer-lead", SummaryLeaderId="lead", RequestedDueAt=DateTimeOffset.UtcNow.AddDays(2) };
    var created=await Cmd(lead,null,draft);
    Check((await db.Queryable<WorkTask>().InSingleAsync(created.TaskId)).Title=="检查设备"&&
        (await db.Queryable<WorkGroup>().InSingleAsync(group)).Name=="生产组","SQL: Unicode names round-trip under non-Chinese database collation");
    await Reject(()=>service.DetailAsync(admin,created.TaskId),"scope: platform administrator has no implicit task body access");
    await Reject(()=>Cmd(new WorkActor(Access("mixed")),null,new WorkTaskCommand { Action="save",Title="越权",Requirements="x",AcceptanceCriteria="x",GroupId=group,AssigneeId="employee" }),"scope: roles and scopes do not form a Cartesian privilege union");
    var dispatched=await Cmd(lead,created.TaskId,new() { Action="dispatch",ExpectedVersion=created.Version });
    await Reject(()=>Cmd(lead,created.TaskId,new(){Action="revise",ExpectedVersion=dispatched.Version,Title="不能绕过转派",Requirements="x",AcceptanceCriteria="x",GroupId=group,AssigneeId="other",ReviewerId="peer-lead",SummaryLeaderId="lead",Text="绕过交接"}),"relationships: requirements revision cannot silently replace assignee");
    Check((await db.Queryable<WorkTask>().InSingleAsync(created.TaskId)).AssigneeId=="employee","relationships: rejected revision preserves original responsibility");
    await Reject(()=>Cmd(reviewer,created.TaskId,new(){Action="cancel",ExpectedVersion=dispatched.Version,Text="越权取消"}),"relationships: same-group reviewer cannot modify a task assigned to another manager");
    var dispatchRetry=new WorkTaskCommand { Action="comment",IdempotencyKey="same-request",Text="请说明处理方式" };
    var first=await service.CommandAsync(lead,created.TaskId,dispatchRetry); var second=await service.CommandAsync(lead,created.TaskId,dispatchRetry);
    Check(first==second && await db.Queryable<WorkReport>().CountAsync(x=>x.TaskId==created.TaskId)==1,"idempotency: retry yields one business record");
    dispatchRetry.Text="different"; await Reject(()=>service.CommandAsync(lead,created.TaskId,dispatchRetry),"idempotency: same key with different content rejected");
    await Reject(()=>service.DetailAsync(other,created.TaskId),"scope: another employee cannot read task ID");
    var plan=await Cmd(employee,created.TaskId,new() { Action="assess",ExpectedVersion=dispatched.Version,Text="可以完成",DueAt=DateTimeOffset.UtcNow.AddDays(1),Milestones=[new() { Title="现场排查",DueAt=DateTimeOffset.UtcNow.AddHours(4) }] });
    var before=await db.Queryable<WorkTask>().InSingleAsync(created.TaskId);
    Check(before.Status=="InProgress"&&before.OriginalDueAtUtc.HasValue,"workflow: assessment establishes plan and original commitment");
    var delayed=await Cmd(employee,created.TaskId,new() { Action="assess",ExpectedVersion=plan.Version,Text="需要延期",DueAt=DateTimeOffset.UtcNow.AddDays(3) });
    Check((await db.Queryable<WorkTask>().InSingleAsync(created.TaskId)).CommittedDueAtUtc==before.CommittedDueAtUtc,"workflow: unapproved extension does not replace current promise");
    var approved=await Cmd(lead,created.TaskId,new() { Action="plan.approve",ExpectedVersion=delayed.Version,RecordId=delayed.RecordId });
    Check((await db.Queryable<WorkTask>().InSingleAsync(created.TaskId)).OriginalDueAtUtc==before.OriginalDueAtUtc,"workflow: approved extension retains original promise");
    var report=await Cmd(employee,created.TaskId,new() { Action="report",Text="排查完成，整理说明",Progress=100,Blocked=false });
    Check((await db.Queryable<WorkTask>().InSingleAsync(created.TaskId)).Status=="InProgress","workflow: progress 100 is not formal completion");
    var upload=J(await service.BeginUploadAsync(employee,new() { TaskId=created.TaskId,Name="说明.txt",Size=Encoding.UTF8.GetByteCount("actual report") }));
    await service.UploadAsync(upload.GetProperty("uploadId").GetString()!,upload.GetProperty("ticket").GetString()!,new MemoryStream(Encoding.UTF8.GetBytes("actual report")),default);
    var attachment=J(await service.CompleteUploadAsync(employee,upload.GetProperty("uploadId").GetString()!)).GetProperty("id").GetString()!;
    await Reject(()=>service.DownloadAsync(lead,attachment),"files: unshared uploaded file is private before submission");
    var saved=await Cmd(employee,created.TaskId,new() { Action="submission.save",ExpectedVersion=approved.Version,Text="真实处理说明",AttachmentIds=[attachment] });
    await Cmd(employee,created.TaskId,new(){Action="submission.save",ExpectedVersion=saved.Version,RecordId=saved.RecordId,RecordVersion=1,Text="真实处理说明（更正文字）"});
    Check(await db.Queryable<WorkAttachmentLink>().AnyAsync(x=>x.RecordId==saved.RecordId&&x.AttachmentId==attachment),"drafts: text-only update preserves omitted attachment associations");
    var leaderDetail=J(await service.DetailAsync(lead,created.TaskId));
    Check(leaderDetail.GetProperty("submissions").GetArrayLength()==0&&!leaderDetail.GetRawText().Contains("真实处理说明"),"privacy: draft body and filenames are not leaked through detail or audit");
    await Reject(()=>Cmd(other,created.TaskId,new() { Action="submission.save",ExpectedVersion=approved.Version,Text="冒领",AttachmentIds=[attachment] }),"files: another employee cannot attach or submit materials");
    var submitted=await Cmd(employee,created.TaskId,new() { Action="submit",ExpectedVersion=saved.Version,RecordId=saved.RecordId,RecordVersion=2 });
    Check((await db.Queryable<WorkTask>().InSingleAsync(created.TaskId)).Status=="PendingReview","workflow: formal submission awaits review");
    Check(File.Exists((await service.DownloadAsync(lead,attachment)).Path),"files: authorized reviewer can download shared version");
    Check(J(await service.DetailAsync(reviewer,created.TaskId)).GetProperty("canReview").GetBoolean()&&!J(await service.DetailAsync(reviewer,created.TaskId)).GetProperty("canManage").GetBoolean(),"relationships: designated reviewer has review ability without general task management");
    var returned=await Cmd(reviewer,created.TaskId,new() { Action="review.return",ExpectedVersion=submitted.Version,RecordId=saved.RecordId,Text="请补充结果" });
    var revised=await Cmd(employee,created.TaskId,new() { Action="submission.save",ExpectedVersion=returned.Version,Text="补充处理结果",AttachmentIds=[attachment] });
    var resubmitted=await Cmd(employee,created.TaskId,new() { Action="submit",ExpectedVersion=revised.Version,RecordId=revised.RecordId,RecordVersion=1 });
    await Cmd(reviewer,created.TaskId,new() { Action="review.pass",ExpectedVersion=resubmitted.Version,RecordId=revised.RecordId,Text="符合交付标准" });
    Check((await db.Queryable<WorkTask>().InSingleAsync(created.TaskId)).CompletedAtUtc.HasValue&&await db.Queryable<WorkSubmission>().CountAsync(x=>x.TaskId==created.TaskId)==2,"workflow: return and resubmit preserve history and complete correctly");
    await Reject(()=>Cmd(lead,created.TaskId,new(){Action="cancel",ExpectedVersion=1,Text="stale"}),"concurrency: stale structural command rejected");
    var handoff=await Cmd(lead,null,new(){Action="save",Title="后续现场处理",Requirements="记录现场结果",AcceptanceCriteria="文字或图片说明",GroupId=group,AssigneeId="employee",ReviewerId="lead",SummaryLeaderId="lead"});
    var sentHandoff=await Cmd(lead,handoff.TaskId,new(){Action="dispatch",ExpectedVersion=handoff.Version});
    var ownHandoffReceipt=await Cmd(employee,handoff.TaskId,new(){Action="report",Text="准备交接"});
    await Reject(()=>service.SaveMemberAsync(admin,new(){GroupId=group,UserId="employee",Enabled=false}),"handover: member with unfinished task cannot be silently removed");
    var reassigned=await Cmd(lead,handoff.TaskId,new(){Action="reassign",ExpectedVersion=sentHandoff.Version,AssigneeId="other",Text="安排交接"});
    await Reject(()=>service.DetailAsync(employee,handoff.TaskId),"handover: former assignee loses task access");
    await Reject(()=>service.OperationAsync(employee,ownHandoffReceipt.OperationId),"receipts: operation lookup does not bypass current permissions");
    await Cmd(other,handoff.TaskId,new(){Action="assess",ExpectedVersion=reassigned.Version,Text="已承接",DueAt=DateTimeOffset.UtcNow.AddDays(1)});
    var activeHandoff=await db.Queryable<WorkTask>().InSingleAsync(handoff.TaskId);
    await db.Updateable<SysUserInfo>().SetColumns(x=>new SysUserInfo{Enabled=false}).Where(x=>x.UserId=="lead").ExecuteCommandAsync();
    var retired=J(await service.CandidatesAsync(admin,new(){Purpose="dispatcher",TaskId=handoff.TaskId,SelectedId="lead"}));
    Check(retired.GetProperty("selected").GetProperty("disabled").GetBoolean()&&retired.GetProperty("selected").GetProperty("value").GetString()=="lead","candidates: retired current manager remains visible with disabled reason");
    await service.HandoverAsync(admin,new(){TaskId=handoff.TaskId,ExpectedVersion=activeHandoff.Version,DispatcherId="peer-lead",ReviewerId="peer-lead",SummaryLeaderId="peer-lead",Reason="原领导停用，明确交接管理责任"});
    Check(J(await service.DetailAsync(reviewer,handoff.TaskId)).GetProperty("canManage").GetBoolean(),"handover: scoped administrator can recover orphaned management without reading task body");
    await db.Updateable<SysUserInfo>().SetColumns(x=>new SysUserInfo{Enabled=true}).Where(x=>x.UserId=="lead").ExecuteCommandAsync();
    Check(!J(await service.DetailAsync(lead,handoff.TaskId)).GetProperty("canManage").GetBoolean(),"handover: old manager loses mutation ability after explicit responsibility transfer");
    await service.SaveGrantAsync(admin,new(){UserId="mixed",Role="work-admin",Scope="Group",TargetId=group});
    await Reject(()=>service.SaveGrantAsync(new WorkActor(Access("mixed")),new(){UserId="mixed",Role="dispatcher",Scope="All"}),"delegation: scoped administrator cannot self-grant wider authority");
    await service.SaveGrantAsync(admin,new(){UserId="observer",Role="work-admin",Scope="User",TargetId="employee"});
    await Reject(()=>service.SaveGrantAsync(new WorkActor(Access("observer")),new(){UserId="employee",Role="dispatcher",Scope="Group",TargetId=group}),"delegation: person-only admin cannot grant authority over an entire group");
    var fileTask=await Cmd(lead,null,new(){Action="save",Title="上传测试任务",Requirements="验证实际材料",AcceptanceCriteria="可识别的文件",GroupId=group,AssigneeId="employee",ReviewerId="lead",SummaryLeaderId="lead"});
    await Cmd(lead,fileTask.TaskId,new(){Action="dispatch",ExpectedVersion=fileTask.Version});
    var badUpload=J(await service.BeginUploadAsync(employee,new(){TaskId=fileTask.TaskId,Name="fake.png",Size=4}));
    await service.UploadAsync(badUpload.GetProperty("uploadId").GetString()!,badUpload.GetProperty("ticket").GetString()!,new MemoryStream(Encoding.UTF8.GetBytes("fake")),default);
    await Reject(()=>service.CompleteUploadAsync(employee,badUpload.GetProperty("uploadId").GetString()!),"files: renamed fake image rejected by actual content");
    await Reject(()=>service.BeginUploadAsync(employee,new(){TaskId=fileTask.TaskId,Name="../escape.txt",Size=4}),"files: traversal filename rejected");
    var key=J(await service.CreateKeyAsync(employee,new() { Label="test-client",Scopes="read,assess,report,submit,upload,download" }));
    var token=key.GetProperty("secret").GetString()!; var personal=await service.AuthenticateKeyAsync(token);
    await Reject(()=>service.DetailAsync(personal,handoff.TaskId),"MCP: former assignee cannot access transferred task with a valid key");
    Check(!J(await service.KeysAsync(employee)).GetRawText().Contains(token),"credentials: plaintext never returned in key listings");
    await Reject(()=>service.ListAsync(personal,new() { View="team" }),"MCP scope: personal credential cannot list team tasks");
    await Reject(()=>service.DetailAsync(new WorkActor(Access("employee","B")),created.TaskId),"application isolation: same user in B cannot read A task");
    await RunHttp();
    Check(J(await service.KeyActivityAsync(employee)).GetRawText().Contains("worktask.my.get")&&!J(await service.KeyActivityAsync(employee)).GetRawText().Contains(token),"credentials: owner can inspect tool usage metadata without plaintext credential or task payloads");
    var nowLocal=TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
    await service.SaveRuleAsync(admin,new() { Enabled=true,DailyTime=nowLocal.ToString("HH:mm"),WeekDays=string.Join(',',Enumerable.Range(0,7)),Channels="Inbox",EmployeeAudience="ActiveOrChanged" });
    await service.TickAsync(default); var count=await db.Queryable<WorkDigest>().CountAsync(); await service.TickAsync(default);
    Check(count>=2&&await db.Queryable<WorkDigest>().CountAsync()==count,"digest: role views generated once despite repeat job execution");
    Check(J(await service.InboxAsync(employee)).EnumerateArray().Any(x=>x.GetProperty("kind").GetString()=="Digest"),"digest: employee sees own persisted summary");
    var originalSnapshot=(await db.Queryable<WorkDigest>().FirstAsync(x=>x.RecipientId=="employee")).SnapshotJson;
    await db.Updateable<WorkTask>().SetColumns(x=>new WorkTask { Title="later change" }).Where(x=>x.Id==created.TaskId).ExecuteCommandAsync();
    Check((await db.Queryable<WorkDigest>().FirstAsync(x=>x.RecipientId=="employee")).SnapshotJson==originalSnapshot,"digest: later changes do not rewrite previous snapshot");
    await service.SaveRuleAsync(admin,new(){Enabled=true,DailyTime=nowLocal.ToString("HH:mm"),WeekDays=string.Join(',',Enumerable.Range(0,7)),Channels="Inbox,Email"});
    var emailInit=new EmailPushDatabaseInitializer(scope);await emailInit.InitializeAsync(default);
    await db.Updateable<SysUserInfo>().SetColumns(x=>new SysUserInfo{Email="employee@example.test"}).Where(x=>x.UserId=="employee").ExecuteCommandAsync();
    await db.Updateable<EmailPushSettingEntity>().SetColumns(x=>new EmailPushSettingEntity{Enabled=true,Host="smtp.example.test",FromAddress="noreply@example.test",NextSendTime=null}).Where(x=>x.Id==1).ExecuteCommandAsync();
    var transport=new FakeTransport();var mail=new EmailPushService(scope,emailInit,new FakeSecrets(),transport,applications);
    using var providers=new ServiceCollection().AddSingleton<IEmailPushService>(mail).BuildServiceProvider();
    var jobs=new WorkTaskService(scope,initializer,applications,Options.Create(new WorkTaskOptions{StorageRoot=storage}),providers);
    var employeeDigest=await db.Queryable<WorkDigest>().FirstAsync(x=>x.RecipientId=="employee");
    var mailJob=new WorkDelivery{AppCode="A",OriginId=employeeDigest.Id,RecipientId="employee",Perspective="Employee",Channel="Email"};
    await db.Insertable(mailJob).ExecuteCommandAsync();
    await Task.WhenAll(jobs.TickAsync(default),jobs.TickAsync(default));
    var mailOwner=WorkTaskChannelPolicy.Prefix+mailJob.Id;
    Check(await db.Queryable<EmailPushTaskEntity>().CountAsync(x=>x.CreatedBy==mailOwner)==1,"notifications: concurrent workers enqueue one real channel task");
    await mail.DispatchAsync(default);await jobs.TickAsync(default);
    Check(transport.Count==1&&(await db.Queryable<WorkDelivery>().InSingleAsync(mailJob.Id)).Status=="Sent","notifications: trusted business source uses existing email queue and synchronizes result (fake transport, no external send)");
    var delivery=await db.Queryable<EmailPushTaskEntity>().FirstAsync(x=>x.CreatedBy==mailOwner);
    Check(delivery.Subject=="工作任务日终状态"&&!J(await mail.GetRecordsAsync(Access("1"),new())).GetRawText().Contains("later change"),"notifications: generic email records expose no task title or body");
    var projectWeChat=typeof(WeChatClawBotService).GetMethod("ToDeliveryDto",BindingFlags.NonPublic|BindingFlags.Static)!;
    var workWeChat=(WeChatClawBotDeliveryDto)projectWeChat.Invoke(null,[new WeChatClawBotDeliveryEntity{Source="WorkTask",Content="private work counts"}])!;
    var ordinaryWeChat=(WeChatClawBotDeliveryDto)projectWeChat.Invoke(null,[new WeChatClawBotDeliveryEntity{Source="AgentPush",Content="ordinary message"}])!;
    Check(!workWeChat.Content.Contains("private work counts")&&ordinaryWeChat.Content=="ordinary message","notifications: generic WeChat records redact work content and preserve ordinary messages");
    var emailDelivery=await db.Queryable<EmailPushDeliveryEntity>().FirstAsync(x=>x.TaskId==delivery.Id);
    await db.Updateable<EmailPushDeliveryEntity>().SetColumns(x=>new EmailPushDeliveryEntity{Status="Failed"}).Where(x=>x.Id==emailDelivery.Id).ExecuteCommandAsync();
    await Reject(()=>mail.RetryAsync(Access("1"),emailDelivery.Id,new(){Confirm=true}),"notifications: generic retry cannot bypass work-task source policy");
    await db.Updateable<WorkDelivery>().SetColumns(x=>new WorkDelivery{Status="Failed"}).Where(x=>x.Id==mailJob.Id).ExecuteCommandAsync();
    await Reject(()=>jobs.RetryDeliveryAsync(employee,mailJob.Id),"notifications: employee cannot retry an administrative delivery");
    await jobs.RetryDeliveryAsync(admin,mailJob.Id);
    Check((await db.Queryable<EmailPushDeliveryEntity>().InSingleAsync(emailDelivery.Id)).Status=="Pending","notifications: business administrator can retry a known failure through authorized source");
    await db.Updateable<WorkGrant>().SetColumns(x=>new WorkGrant{Enabled=false}).Where(x=>x.UserId=="employee"&&x.Role=="worker").ExecuteCommandAsync();
    await db.Updateable<EmailPushSettingEntity>().SetColumns(x=>new EmailPushSettingEntity{NextSendTime=null}).Where(x=>x.Id==1).ExecuteCommandAsync();
    await mail.DispatchAsync(default);
    Check(transport.Count==1&&(await db.Queryable<EmailPushDeliveryEntity>().InSingleAsync(emailDelivery.Id)).Status=="Cancelled","notifications: permission revocation before actual send prevents stale summary delivery");
    await db.Updateable<WorkGrant>().SetColumns(x=>new WorkGrant{Enabled=true}).Where(x=>x.UserId=="employee"&&x.Role=="worker").ExecuteCommandAsync();
    var unusedFile=await db.Queryable<WorkAttachment>().FirstAsync(x=>x.Name=="small.txt");
    var oldFileTime=DateTime.UtcNow.AddDays(-2);
    await db.Updateable<WorkAttachment>().SetColumns(x=>new WorkAttachment{CreatedAtUtc=oldFileTime,ExpiresAtUtc=oldFileTime}).Where(x=>x.Id==unusedFile.Id||x.Id==attachment).ExecuteCommandAsync();
    await service.TickAsync(default);
    Check(!await db.Queryable<WorkAttachment>().AnyAsync(x=>x.Id==unusedFile.Id)&&await db.Queryable<WorkAttachment>().AnyAsync(x=>x.Id==attachment&&x.Status=="Ready"),"files: expired unlinked uploads are reclaimed while submitted attachments remain");
    await service.RevokeKeyAsync(employee,key.GetProperty("id").GetString()!);
    await Reject(()=>service.AuthenticateKeyAsync(token),"credentials: revoked key immediately rejected");
    var temporaryKey=J(await service.CreateKeyAsync(employee,new(){Label="upload-revocation-test",Scopes="read,upload"}));
    var uploadActor=await service.AuthenticateKeyAsync(temporaryKey.GetProperty("secret").GetString()!);
    var openTicket=J(await service.BeginUploadAsync(uploadActor,new(){TaskId=fileTask.TaskId,Name="cancelled.txt",Size=4}));
    await service.RevokeKeyAsync(employee,temporaryKey.GetProperty("id").GetString()!);
    await Reject(()=>service.UploadAsync(openTicket.GetProperty("uploadId").GetString()!,openTicket.GetProperty("ticket").GetString()!,new MemoryStream(Encoding.UTF8.GetBytes("test")),default),"files: key revocation invalidates already-issued upload tickets");
    Check(await db.Queryable<WorkEvent>().CountAsync()>0 && await db.Queryable<WorkReceipt>().CountAsync()>0,"persistence: business events, receipts and history committed");
    Console.WriteLine($"PASS {passed} work-task scenarios; isolated SQL database, real HTTP and MCP SDK transport.");
    if(holdForUi)
    {
        // 仅隔离浏览器验证夹具，覆盖真实人员列表跨页选择，不写业务库。
        foreach(var index in Enumerable.Range(1,45))
        {
            var userId="ui-worker-"+index.ToString("D2");
            await db.Insertable(new SysUserInfo{UserId=userId,UserName=userId,NickName="UI worker "+index,Password="test-only-no-login",Enabled=true,Roles="R_USER",CreateTime=DateTime.Now}).ExecuteCommandAsync();
            await db.Insertable(new SysUserApplicationEntity{UserId=userId,AppCode="A",Enabled=true,PermissionsJson="[]",McpScopesJson="[]",UpdateTime=DateTime.Now}).ExecuteCommandAsync();
            await service.SaveMemberAsync(admin,new WorkMember{GroupId=group,UserId=userId});
            await service.SaveGrantAsync(admin,new WorkGrant{UserId=userId,Role="worker",Scope="Self"});
        }
        Console.WriteLine("WORK_TASK_UI_API="+app!.Urls.Single());
        var minutes=int.TryParse(Environment.GetEnvironmentVariable("WORK_TASK_UI_HOLD_MINUTES"),out var requestedMinutes)?Math.Clamp(requestedMinutes,5,120):20;
        await Task.WhenAny(stopUi.Task,Task.Delay(TimeSpan.FromMinutes(minutes)));
    }

    async Task RunHttp()
    {
        var builder=WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IWorkTaskService>(service);builder.Services.AddSingleton(applications);builder.Services.AddSingleton(scope);
        builder.Services.AddMemoryCache();builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllers(x=>x.Filters.Add<SystemApplicationAccessFilter>()).AddApplicationPart(typeof(WorkTasksController).Assembly).AddNewtonsoftJson();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions,TestAuthentication>("test",_=>{});builder.Services.AddAuthorization();
        builder.Services.AddSingleton(DispatchProxy.Create<IExternalToolService,ExternalProbe>());
        var handlers=typeof(WorkTasksController).Assembly.GetType("calmadmin.Controllers.ExternalTools.ExternalMcpV2Handlers")!;
        var list=handlers.GetMethod("ListToolsAsync",BindingFlags.Static|BindingFlags.NonPublic)!;var call=handlers.GetMethod("CallToolAsync",BindingFlags.Static|BindingFlags.NonPublic)!;
        builder.Services.AddMcpServer().WithHttpTransport(x=>x.Stateless=true)
            .WithListToolsHandler((r,c)=>(ValueTask<ListToolsResult>)list.Invoke(null,[r,c])!)
            .WithCallToolHandler((r,c)=>(ValueTask<CallToolResult>)call.Invoke(null,[r,c])!);
        app=builder.Build();app.UseAuthentication();app.UseAuthorization();app.UseMiddleware<WorkTaskPersonalAccessMiddleware>();app.MapControllers();
        app.MapPost("/__test/stop",()=>{stopUi.TrySetResult();return Results.Ok();});
        app.MapMcp("/mcp/work-tasks").AllowAnonymous();app.MapMcp("/mcp/v2").AllowAnonymous();await app.StartAsync();
        using var http=new HttpClient { BaseAddress=new Uri(app.Urls.Single()) };http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        Check((await http.GetAsync("/api/work-tasks")).StatusCode==HttpStatusCode.Unauthorized,"HTTP: web task API requires login");
        Check((await Rpc("/mcp/work-tasks","tools/list",new{})).StatusCode==HttpStatusCode.Unauthorized,"MCP: anonymous discovery rejected before tools execute");
        var old=await (await Rpc("/mcp/v2","tools/list",new{})).Content.ReadAsStringAsync();
        Check(!old.Contains("worktask."),"MCP: old anonymous registry does not enumerate personal tools");
        http.DefaultRequestHeaders.Authorization=new("Bearer",token);
        var initialization=await Rpc("/mcp/work-tasks","initialize",new { protocolVersion="2025-06-18",capabilities=new{},clientInfo=new{name="work-task-smoke",version="1"} });
        Check(initialization.IsSuccessStatusCode,"MCP: authenticated SDK initialization");
        var listing=await (await Rpc("/mcp/work-tasks","tools/list",new{})).Content.ReadAsStringAsync();
        Check(listing.Contains("worktask.my.list")&&!listing.Contains("smoke.read"),"MCP: personal endpoint only advertises isolated task tools");
        await Cmd(employee,fileTask.TaskId,new(){Action="report",Text="保留当前状态",Progress=67,Blocked=true,ForecastDueAt=DateTimeOffset.UtcNow.AddDays(1)});
        var beforeTextReport=await db.Queryable<WorkTask>().InSingleAsync(fileTask.TaskId);
        var textReport=await(await Rpc("/mcp/work-tasks","tools/call",new{name="worktask.progress.report",arguments=new{taskId=fileTask.TaskId,idempotencyKey="text-only-report",text="仅补充文字说明，不重置已有进度"}})).Content.ReadAsStringAsync();
        Check(RpcData(textReport).GetProperty("taskId").GetString()==fileTask.TaskId&&
            (await db.Queryable<WorkTask>().InSingleAsync(fileTask.TaskId)).Progress==67&&
            (await db.Queryable<WorkTask>().InSingleAsync(fileTask.TaskId)).Blocked&&
            (await db.Queryable<WorkTask>().InSingleAsync(fileTask.TaskId)).ForecastDueAtUtc==beforeTextReport.ForecastDueAtUtc,
            "MCP: text-only report preserves omitted progress, blocker and forecast fields");
        var own=await (await Rpc("/mcp/work-tasks","tools/call",new { name="worktask.my.get",arguments=new { taskId=created.TaskId } })).Content.ReadAsStringAsync();
        Check(own.Contains(created.TaskId)&&!own.Contains("\"isError\":true"),"MCP: real SDK tools invoke same persisted task service");
        var uploadResponse=await (await Rpc("/mcp/work-tasks","tools/call",new {name="worktask.attachment.begin",arguments=new {taskId=fileTask.TaskId,name="mcp-file.txt",size=4}})).Content.ReadAsStringAsync();
        var uploadData=RpcData(uploadResponse);
        using var put=new HttpRequestMessage(HttpMethod.Put,"/api/work-task-uploads/"+uploadData.GetProperty("uploadId").GetString()+"/content") {Content=new ByteArrayContent(Encoding.UTF8.GetBytes("test"))};
        put.Headers.Add("X-Work-Upload-Ticket",uploadData.GetProperty("ticket").GetString());
        Check((await http.SendAsync(put)).IsSuccessStatusCode,"MCP files: ticket authorizes actual HTTP binary upload without web session");
        var completed=await (await Rpc("/mcp/work-tasks","tools/call",new {name="worktask.attachment.complete",arguments=new {uploadId=uploadData.GetProperty("uploadId").GetString()}})).Content.ReadAsStringAsync();
        Check(RpcData(completed).GetProperty("name").GetString()=="mcp-file.txt","MCP files: complete validates uploaded bytes and returns attachment ID");
        var small=await(await Rpc("/mcp/work-tasks","tools/call",new{name="worktask.attachment.upload_small",arguments=new{taskId=fileTask.TaskId,name="small.txt",base64=Convert.ToBase64String(Encoding.UTF8.GetBytes("small"))}})).Content.ReadAsStringAsync();
        Check(RpcData(small).GetProperty("name").GetString()=="small.txt","MCP files: bounded small-file compatibility tool works over SDK transport");
        var denied=await (await Rpc("/mcp/work-tasks","tools/call",new { name="smoke.read",arguments=new{} })).Content.ReadAsStringAsync();
        Check(denied.Contains("\"isError\":true")&&!denied.Contains("legacy-only"),"MCP: personal endpoint refuses general anonymous registry tool");
        http.DefaultRequestHeaders.Authorization=null;http.DefaultRequestHeaders.Add("X-Test-User","employee");
        var details=await http.GetFromJsonAsync<JsonElement>("/api/work-tasks/"+created.TaskId);
        Check(details.GetProperty("data").GetProperty("task").TryGetProperty("originalDueAtUtc",out _),"HTTP: camelCase contract and persisted original deadline");
        if(!holdForUi){await app.StopAsync();await app.DisposeAsync();app=null;}
        Task<HttpResponseMessage> Rpc(string path,string method,object parameters)=>http.PostAsJsonAsync(path,new {jsonrpc="2.0",id=Guid.NewGuid().ToString("N"),method,@params=parameters});
    }
    async Task<WorkCommandResult> Cmd(WorkActor actor,string? id,WorkTaskCommand command){command.IdempotencyKey=Guid.NewGuid().ToString("N");return await service.CommandAsync(actor,id,command);}
    async Task Reject(Func<Task> action,string title){try{await action();throw new Exception("Expected rejection: "+title);}catch(Exception ex) when(ex is UnauthorizedAccessException or ArgumentException or WorkConflictException){Check(true,title);}}
    void Check(bool ok,string title){if(!ok)throw new Exception("FAILED: "+title);passed++;Console.WriteLine("PASS "+title);}
    JsonElement RpcData(string body)
    {
        var json=body.TrimStart().StartsWith("{")?body:body.Split('\n').First(x=>x.StartsWith("data:"))[5..];
        using var document=JsonDocument.Parse(json);
        return document.RootElement.GetProperty("result").GetProperty("structuredContent").Clone();
    }
}
finally
{
    if(app!=null){await app.StopAsync();await app.DisposeAsync();}scope?.Dispose();SqlConnection.ClearAllPools();
    await new SqlCommand($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]",master).ExecuteNonQueryAsync();
    if(Directory.Exists(storage))
    {
        var resolved=Path.GetFullPath(storage);
        if(!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||Path.GetFileName(resolved)!=database)throw new InvalidOperationException("Unexpected test cleanup path");
        Directory.Delete(resolved,true);
    }
}
static ApplicationAccessContext Access(string user,string app="A")=>new(){UserId=user,UserName=user,AppCode=app,PlatformAdministrator=user=="1",AllResources=true,Permissions=new(StringComparer.OrdinalIgnoreCase){"*"}};
static JsonElement J(object value)=>JsonSerializer.SerializeToElement(value,WorkTaskJson.Options);

public class ApplicationProbe:DispatchProxy
{
    public static Func<string,string,Task<ApplicationAccessContext>> Resolve=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    { "GetChannelAccessAsync"=>Resolve((string)args![0]!, (string)args[1]!),"GetAccessAsync"=>Resolve(((ClaimsPrincipal)args![0]!).Identity?.Name??"","A"),_=>throw new NotSupportedException(method?.Name) };
}
public class ExternalProbe:DispatchProxy
{
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    { "GetDefinitions"=>new ExternalToolDefinitionDto[]{new(){Name="smoke.read",Title="smoke",RequiredScope="read",InputSchema=JsonSerializer.SerializeToElement(new{type="object"})}},
        "ExecuteAsync"=>Task.FromResult(new ExternalToolExecutionResultDto{Success=(string)args![0]! =="smoke.read",Message="unknown tool",Data=new{Marker="legacy-only"}}),_=>throw new NotSupportedException(method?.Name) };
}
public class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder):AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user=Request.Headers["X-Test-User"].ToString();
        if(user.Length==0&&Request.Headers.Authorization.ToString().StartsWith("Bearer mock."))
        {
            var payload=Request.Headers.Authorization.ToString().Split('.')[1];
            try{user=JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.GetProperty("uiUser").GetString()??"";}catch{user="";}
        }
        return Task.FromResult(user.Length==0?AuthenticateResult.NoResult():AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name,user)],"test")),"test")));
    }
}
public sealed class FakeSecrets:IEmailSecretProtector
{
    public string Protect(string value)=>"test-protected:"+value;
    public string Unprotect(string value)=>value;
}
public sealed class FakeTransport:IEmailTransport
{
    public int Count;
    public Task TestAsync(EmailPushSettingEntity setting,CancellationToken cancellationToken)=>Task.CompletedTask;
    public Task<EmailTransportResult> SendAsync(EmailPushSettingEntity setting,EmailPushTaskEntity task,EmailPushDeliveryEntity delivery,CancellationToken cancellationToken)
    {Interlocked.Increment(ref Count);return Task.FromResult(new EmailTransportResult{Status="Sent"});}
}

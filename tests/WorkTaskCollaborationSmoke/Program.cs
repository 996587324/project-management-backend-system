using System.Reflection;
using System.Text;
using System.Text.Json;
using calmadmin.WorkTasks;
using Interface;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Model.SystemApplications;
using Model.User;
using Model.WorkTasks;
using Service;
using SqlSugar;
var name="CodexCollab_"+Guid.NewGuid().ToString("N");var server=@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false;Connect Timeout=15";
await using var master=new SqlConnection(new SqlConnectionStringBuilder(server){InitialCatalog="master"}.ConnectionString);await master.OpenAsync();await new SqlCommand($"CREATE DATABASE [{name}]",master).ExecuteNonQueryAsync();
var storage=Path.Combine(Path.GetTempPath(),name);
try {
 var scope=new SqlSugarScope(new ConnectionConfig{ConfigId="calmadmin",DbType=SqlSugar.DbType.SqlServer,InitKeyType=InitKeyType.Attribute,IsAutoCloseConnection=true,ConnectionString=new SqlConnectionStringBuilder(server){InitialCatalog=name}.ConnectionString});
 await new SystemDatabaseInitializer(scope,LoggerFactory.Create(_=>{}).CreateLogger<SystemDatabaseInitializer>()).InitializeAsync(default);var db=scope.GetConnectionScope("calmadmin");var init=new WorkTaskInitializer(scope);await init.InitializeAsync(default);await new WorkTaskInitializer(scope).InitializeAsync(default);
 Check(db.DbMaintenance.IsAnyTable("work_task_participant",false),"participant table and repeated initialization");
 foreach(var id in new[]{"leader","owner","helper","outsider"}) {await db.Insertable(new SysUserInfo{UserId=id,UserName=id,NickName=id,Password="test",Enabled=true,Roles="R_USER",CreateTime=DateTime.Now}).ExecuteCommandAsync();await db.Insertable(new SysUserApplicationEntity{AppCode="A",UserId=id,Enabled=true,PermissionsJson="[]",McpScopesJson="[]",UpdateTime=DateTime.Now}).ExecuteCommandAsync();}
 await db.Insertable(new WorkGroup{Id="g",Name="小组"}).ExecuteCommandAsync();
 foreach(var id in new[]{"owner","helper","outsider"}) {await db.Insertable(new WorkMember{GroupId="g",UserId=id}).ExecuteCommandAsync();await db.Insertable(new WorkGrant{UserId=id,Role="worker",Scope="Self"}).ExecuteCommandAsync();}
 await db.Insertable(new WorkGrant{UserId="leader",Role="dispatcher",Scope="Group",TargetId="g"}).ExecuteCommandAsync();
 var app=DispatchProxy.Create<ISystemApplicationService,Probe>();var service=new WorkTaskService(scope,init,app,Options.Create(new WorkTaskOptions{StorageRoot=storage}));
 WorkActor A(string id)=>new(new ApplicationAccessContext{AppCode="A",UserId=id});
 var lead=A("leader");var owner=A("owner");var helper=A("helper");var outsider=A("outsider");
 var draft=await service.CommandAsync(lead,null,new(){Action="save",IdempotencyKey="create",Title="共享任务",Requirements="要求",AcceptanceCriteria="交付",GroupId="g",AssigneeId="owner",CollaboratorIds=["helper"],RequiresReview=true});
 var sent=await service.CommandAsync(lead,draft.TaskId,new(){Action="dispatch",IdempotencyKey="dispatch",ExpectedVersion=draft.Version});
 Check((await db.Queryable<WorkTaskParticipant>().FirstAsync(x=>x.TaskId==draft.TaskId&&x.UserId=="helper")).Enabled,"collaborator persisted on task");
 var helperView=JsonSerializer.SerializeToElement(await service.ListAsync(helper,new(){View="my"}),WorkTaskJson.Options);
 Check(helperView.GetProperty("total").GetInt32()==1,"collaborator sees shared task in my list");
 try {await service.DetailAsync(outsider,draft.TaskId);throw new Exception("outsider access");} catch(UnauthorizedAccessException){Check(true,"nonparticipant denied task detail");}
 var detail=JsonSerializer.SerializeToElement(await service.DetailAsync(helper,draft.TaskId),WorkTaskJson.Options);
 Check(detail.GetProperty("canParticipate").GetBoolean()&&!detail.GetProperty("canWork").GetBoolean(),"collaborator may contribute but not submit or assess");
 var helperKey=JsonSerializer.SerializeToElement(await service.CreateKeyAsync(helper,new(){Label="helper",Scopes="read,report,upload",Days=1}),WorkTaskJson.Options);
 var personal=await service.AuthenticateKeyAsync(helperKey.GetProperty("secret").GetString()!);
 Check((await WorkTaskMcpTools.Call(service,personal,"worktask.my.get",JsonSerializer.SerializeToElement(new{taskId=draft.TaskId}))).IsError!=true,"personal MCP reads assigned collaboration");
 try {await service.CommandAsync(helper,draft.TaskId,new(){Action="assess",IdempotencyKey="bad-assess",ExpectedVersion=sent.Version,Text="no"});throw new Exception("helper can assess");} catch(UnauthorizedAccessException){Check(true,"only owner assesses shared task");}
 var file=JsonSerializer.SerializeToElement(await service.BeginUploadAsync(helper,new(){TaskId=draft.TaskId,Name="helper.txt",Size=4}),WorkTaskJson.Options);
 await service.UploadAsync(file.GetProperty("uploadId").GetString()!,file.GetProperty("ticket").GetString()!,new MemoryStream(Encoding.UTF8.GetBytes("data")),default);
 var ready=JsonSerializer.SerializeToElement(await service.CompleteUploadAsync(helper,file.GetProperty("uploadId").GetString()!),WorkTaskJson.Options);var fileId=ready.GetProperty("id").GetString()!;
 await service.CommandAsync(helper,draft.TaskId,new(){Action="report",IdempotencyKey="report",Text="协作完成",Progress=75,AttachmentIds=[fileId]});
 Check((await db.Queryable<WorkTask>().InSingleAsync(draft.TaskId)).Progress==0,"helper progress does not overwrite overall progress");
 Check((await service.DownloadAsync(owner,fileId)).Name=="helper.txt","owner can read shared collaborator file");
 var plan=await service.CommandAsync(owner,draft.TaskId,new(){Action="assess",IdempotencyKey="assess",ExpectedVersion=sent.Version,Text="可完成",DueAt=DateTimeOffset.UtcNow.AddDays(1)});
 var sub=await service.CommandAsync(owner,draft.TaskId,new(){Action="submission.save",IdempotencyKey="submission",ExpectedVersion=plan.Version,Text="汇总",AttachmentIds=[fileId]});
 await service.CommandAsync(owner,draft.TaskId,new(){Action="submit",IdempotencyKey="submit",ExpectedVersion=sub.Version,RecordId=sub.RecordId,RecordVersion=1});
 Check((await db.Queryable<WorkTask>().InSingleAsync(draft.TaskId)).Status=="PendingReview","owner submits shared file for review");

 var overview=JsonSerializer.SerializeToElement(await service.OverviewAsync(helper,new(){UserId="helper"}),WorkTaskJson.Options);
 Check(overview.GetProperty("total").GetInt32()==1,"overview employee filter includes collaborator without duplicate tasks");
 var local=TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
 await db.Insertable(new WorkNotificationRule{AppCode="A",Enabled=true,Channels="Inbox",DailyTime=local.ToString("HH:mm"),WeekDays="0,1,2,3,4,5,6"}).ExecuteCommandAsync();
 await service.TickAsync(default);
 var helperInbox=JsonSerializer.SerializeToElement(await service.InboxAsync(helper),WorkTaskJson.Options);
 Check(helperInbox.EnumerateArray().Any(x=>x.GetProperty("kind").GetString()=="Event") && helperInbox.EnumerateArray().Any(x=>x.GetProperty("kind").GetString()=="Digest"),"collaborator receives task event and daily summary");

 var dispatchEvent=await db.Queryable<WorkEvent>().FirstAsync(x=>x.TaskId==draft.TaskId&&x.Action=="dispatch");
 await db.Updateable<WorkEvent>().SetColumns(x=>x.PushRequested==true).Where(x=>x.Id==dispatchEvent.Id).ExecuteCommandAsync();
 await db.Updateable<WorkNotificationRule>().SetColumns(x=>x.Channels=="Inbox,WeChat").Where(x=>x.AppCode=="A").ExecuteCommandAsync();
 var push=new WorkDelivery{AppCode="A",OriginId=dispatchEvent.Id,RecipientId="helper",Perspective="Employee",Channel="WeChat",Status="Pending"};await db.Insertable(push).ExecuteCommandAsync();
 Check(await WorkTaskChannelPolicy.AuthorizeAsync(db,"work:"+push.Id,"A","helper","WeChat"),"dispatch push authorizes active collaborator");
 var current=await db.Queryable<WorkTask>().InSingleAsync(draft.TaskId);
 await service.CommandAsync(lead,draft.TaskId,new(){Action="participants.change",IdempotencyKey="remove",ExpectedVersion=current.Version,CollaboratorIds=[]});
 try {await service.DetailAsync(helper,draft.TaskId);throw new Exception("removed participant still sees task");} catch(UnauthorizedAccessException){Check(true,"removed participant loses access");}
 Check((await db.Queryable<WorkTaskParticipant>().FirstAsync(x=>x.TaskId==draft.TaskId&&x.UserId=="helper")).Enabled==false,"removed membership retained for history");
 Check(!await WorkTaskChannelPolicy.AuthorizeAsync(db,"work:"+push.Id,"A","helper","WeChat"),"removed collaborator cannot receive pending task push");
 var old=await service.CommandAsync(lead,null,new(){Action="save",IdempotencyKey="old",Title="单人历史",GroupId="g",AssigneeId="owner"});
 Check((await db.Queryable<WorkTask>().InSingleAsync(old.TaskId)).AssigneeId=="owner","single-person task remains compatible");
 Console.WriteLine("PASS focused task collaboration scenarios");
} finally {SqlConnection.ClearAllPools();await new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]",master).ExecuteNonQueryAsync();if(Directory.Exists(storage))Directory.Delete(storage,true);}
static void Check(bool b,string m){if(!b)throw new Exception("FAIL: "+m);Console.WriteLine("PASS: "+m);}
public class Probe:DispatchProxy {protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name=="GetChannelAccessAsync"?Task.FromResult(new ApplicationAccessContext{UserId=(string)args![0]!,AppCode=(string)args[1]!}):throw new NotSupportedException();}

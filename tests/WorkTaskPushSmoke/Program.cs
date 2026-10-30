using System.Reflection;
using System.Text.Json;
using Interface;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Model.EmailPush;
using Model.SystemApplications;
using Model.User;
using Model.WeChatClawBot;
using Model.WorkTasks;
using Service;
using SqlSugar;

var name="CodexPush_"+Guid.NewGuid().ToString("N");
var server=@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false;Connect Timeout=15";
await using var master=new SqlConnection(new SqlConnectionStringBuilder(server){InitialCatalog="master"}.ConnectionString);
await master.OpenAsync(); await new SqlCommand($"CREATE DATABASE [{name}]",master).ExecuteNonQueryAsync();
try {
 var scope=new SqlSugarScope(new ConnectionConfig{ConfigId="calmadmin",DbType=SqlSugar.DbType.SqlServer,InitKeyType=InitKeyType.Attribute,IsAutoCloseConnection=true,ConnectionString=new SqlConnectionStringBuilder(server){InitialCatalog=name}.ConnectionString});
 await new SystemDatabaseInitializer(scope,LoggerFactory.Create(_=>{}).CreateLogger<SystemDatabaseInitializer>()).InitializeAsync(default);
 var db=scope.GetConnectionScope("calmadmin");var init=new WorkTaskInitializer(scope);await init.InitializeAsync(default);
 await db.Insertable(new WorkEvent{Id="old-event",TaskId="old",Action="legacy",SnapshotJson="{}",Processed=true}).ExecuteCommandAsync();
 await db.Ado.ExecuteCommandAsync("DECLARE @n sysname; SELECT @n=dc.name FROM sys.default_constraints dc JOIN sys.columns c ON c.default_object_id=dc.object_id WHERE dc.parent_object_id=OBJECT_ID('work_task_outbox') AND c.name='PushRequested'; IF @n IS NOT NULL EXEC ('ALTER TABLE [work_task_outbox] DROP CONSTRAINT [' + @n + ']');");
 await db.Ado.ExecuteCommandAsync("ALTER TABLE [work_task_outbox] DROP COLUMN [PushRequested]");
 await new WorkTaskInitializer(scope).InitializeAsync(default);await new WorkTaskInitializer(scope).InitializeAsync(default);
 Check(await db.Ado.GetIntAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='work_task_outbox' AND COLUMN_NAME='PushRequested' AND IS_NULLABLE='NO'")==1 && !(await db.Queryable<WorkEvent>().InSingleAsync("old-event")).PushRequested,"existing outbox migration preserves old event and disables push");
 foreach(var id in new[]{"leader","employee"}) {
  await db.Insertable(new SysUserInfo{UserId=id,UserName=id,NickName=id,Password="test",Email=id+"@example.test",Enabled=true,Roles="R_USER",CreateTime=DateTime.Now}).ExecuteCommandAsync();
  await db.Insertable(new SysUserApplicationEntity{AppCode="A",UserId=id,Enabled=true,PermissionsJson="[]",McpScopesJson="[]",UpdateTime=DateTime.Now}).ExecuteCommandAsync();
 }
 await db.Insertable(new WorkGroup{Id="g",Name="测试组"}).ExecuteCommandAsync();
 await db.Insertable(new WorkMember{GroupId="g",UserId="employee"}).ExecuteCommandAsync();
 await db.Insertable(new[]{new WorkGrant{UserId="leader",Role="dispatcher",Scope="Group",TargetId="g"},new WorkGrant{UserId="employee",Role="worker",Scope="Self"}}).ExecuteCommandAsync();
 var apps=DispatchProxy.Create<ISystemApplicationService,Probe>();
 var access=new ApplicationAccessContext{AppCode="A",UserId="leader"};var actor=new WorkActor(access);
 var service=new WorkTaskService(scope,init,apps,Options.Create(new WorkTaskOptions()));
 async Task<WorkCommandResult> Draft(string key)=>await service.CommandAsync(actor,null,new(){Action="save",IdempotencyKey=key,Title="新任务",Requirements="敏感测试详情XYZ",AcceptanceCriteria="完成说明",GroupId="g",AssigneeId="employee",RequiresReview=false});
 var noConfig=await Draft("draft0");
 try {await service.CommandAsync(actor,noConfig.TaskId,new(){Action="dispatch",IdempotencyKey="send0",ExpectedVersion=noConfig.Version,PushOnDispatch=true});throw new Exception("missing channels accepted");} catch(ArgumentException){Check((await db.Queryable<WorkTask>().InSingleAsync(noConfig.TaskId)).Status=="Draft","missing push channels reject dispatch without partial state");}
 var local=TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
 await db.Insertable(new WorkNotificationRule{AppCode="A",Enabled=false,Channels="Inbox,Email,WeChat",DailyTime=local.ToString("HH:mm"),WeekDays="0,1,2,3,4,5,6"}).ExecuteCommandAsync();
 var emailInit=new EmailPushDatabaseInitializer(scope);await emailInit.InitializeAsync(default);
 await db.Updateable<EmailPushSettingEntity>().SetColumns(x=>new EmailPushSettingEntity{Enabled=true,Host="smtp.example.test",FromAddress="noreply@example.test",NextSendTime=null}).Where(x=>x.Id==1).ExecuteCommandAsync();
 WeChatClawBotDatabaseInitializer.Initialize(db);var wxInit=new WeChatPushQueueInitializer(scope);await wxInit.InitializeAsync(default);
 await db.Insertable(new WeChatClawBotAccountEntity{UserId="employee",UserName="employee",Status="Bound",BotToken="test-token",McpPushEnabled=true}).ExecuteCommandAsync();
 await db.Insertable(new WeChatClawBotConversationEntity{UserId="employee",ContextToken="test-context"}).ExecuteCommandAsync();
 var transport=new FakeTransport();var mail=new EmailPushService(scope,emailInit,new FakeSecrets(),transport,apps);
 var wx=new WeChatPushQueueService(scope,wxInit,apps,DispatchProxy.Create<IWeChatClawBotService,Probe>());
 using var providers=new ServiceCollection().AddSingleton<IEmailPushService>(mail).AddSingleton<IWeChatPushQueueService>(wx).BuildServiceProvider();
 var jobs=new WorkTaskService(scope,init,apps,Options.Create(new WorkTaskOptions()),providers);
 var quiet=await Draft("draft1");await service.CommandAsync(actor,quiet.TaskId,new(){Action="dispatch",IdempotencyKey="send1",ExpectedVersion=quiet.Version});
 var loud=await Draft("draft2");await service.CommandAsync(actor,loud.TaskId,new(){Action="dispatch",IdempotencyKey="send2",ExpectedVersion=loud.Version,PushOnDispatch=true});
 await jobs.TickAsync(default);await jobs.TickAsync(default);
 var ev=await db.Queryable<WorkEvent>().FirstAsync(x=>x.TaskId==loud.TaskId&&x.Action=="dispatch");
 var quietEv=await db.Queryable<WorkEvent>().FirstAsync(x=>x.TaskId==quiet.TaskId&&x.Action=="dispatch");
 Check(await db.Queryable<WorkDelivery>().CountAsync(x=>x.OriginId==ev.Id&&x.Channel!="Inbox")==2,"selected dispatch creates one delivery per configured channel");
 Check(await db.Queryable<WorkDelivery>().CountAsync(x=>x.OriginId==quietEv.Id&&x.Channel!="Inbox")==0,"unselected dispatch creates no external delivery");
 Check(await db.Queryable<WorkDigest>().CountAsync()==0,"daily summary can remain disabled while dispatch push is enabled");
 var email=await db.Queryable<WorkDelivery>().FirstAsync(x=>x.OriginId==ev.Id&&x.Channel=="Email");
 var wechat=await db.Queryable<WorkDelivery>().FirstAsync(x=>x.OriginId==ev.Id&&x.Channel=="WeChat");
 Check(email.Status=="Queued"&&wechat.Status=="Queued","dispatch enters real email and WeChat queues");
 Check(await db.Queryable<EmailPushTaskEntity>().CountAsync(x=>x.CreatedBy=="work:"+email.Id)==1&&await db.Queryable<WeChatPushQueueEntity>().CountAsync(x=>x.CreatedBy=="work:"+wechat.Id)==1,"repeat processing does not duplicate channel jobs");
 var m=await db.Queryable<EmailPushTaskEntity>().FirstAsync(x=>x.CreatedBy=="work:"+email.Id);
 Check(m.Subject=="工作任务下派提醒"&&!m.Body.Contains("敏感测试详情XYZ"),"dispatch message contains no task content");
 await mail.DispatchAsync(default);await jobs.TickAsync(default);
 Check(transport.Count==1&&(await db.Queryable<WorkDelivery>().InSingleAsync(email.Id)).Status=="Sent","email fake transport and delivery status synchronize");
 await db.Updateable<WorkGrant>().SetColumns(x=>x.Enabled==false).Where(x=>x.UserId=="employee"&&x.Role=="worker").ExecuteCommandAsync();
 Check(!await WorkTaskChannelPolicy.AuthorizeAsync(db,"work:"+wechat.Id,"A","employee","WeChat"),"permission revoked before send blocks WeChat");
 await db.Updateable<WorkGrant>().SetColumns(x=>x.Enabled==true).Where(x=>x.UserId=="employee"&&x.Role=="worker").ExecuteCommandAsync();
 await db.Updateable<WorkNotificationRule>().SetColumns(x=>x.Enabled==true).Where(x=>x.AppCode=="A").ExecuteCommandAsync();
 await jobs.TickAsync(default);var count=await db.Queryable<WorkDigest>().CountAsync();await jobs.TickAsync(default);
 Check(count>=1&&count==await db.Queryable<WorkDigest>().CountAsync(),"daily summary works and is generated once");
 Console.WriteLine("PASS focused work task push scenarios");
} finally {SqlConnection.ClearAllPools();await new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]",master).ExecuteNonQueryAsync();}
static void Check(bool b,string m){if(!b)throw new Exception("FAIL: "+m);Console.WriteLine("PASS: "+m);}
public class Probe:DispatchProxy {protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name=="GetChannelAccessAsync" ? Task.FromResult(new ApplicationAccessContext{UserId=(string)args![0]!,AppCode=(string)args[1]!}) : throw new NotSupportedException();}
public sealed class FakeSecrets:IEmailSecretProtector {public string Protect(string v)=>v;public string Unprotect(string v)=>v;}
public sealed class FakeTransport:IEmailTransport {public int Count;public Task TestAsync(EmailPushSettingEntity s,CancellationToken c)=>Task.CompletedTask; public Task<EmailTransportResult> SendAsync(EmailPushSettingEntity s,EmailPushTaskEntity t,EmailPushDeliveryEntity d,CancellationToken c){Count++;return Task.FromResult(new EmailTransportResult{Status="Sent"});}}

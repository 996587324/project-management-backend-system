using System.Reflection;
using calmadmin.WorkTasks;
using System.Text.Json;
using Interface;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Model.Menu;
using Model.SystemApplications;
using Model.User;
using Model.WorkTasks;
using Service;
using SqlSugar;

var name="CodexOverview_"+Guid.NewGuid().ToString("N");
var server=@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false;Connect Timeout=15";
await using var master=new SqlConnection(new SqlConnectionStringBuilder(server){InitialCatalog="master"}.ConnectionString);
await master.OpenAsync(); await new SqlCommand($"CREATE DATABASE [{name}]",master).ExecuteNonQueryAsync();
try {
 var scope=new SqlSugarScope(new ConnectionConfig{ConfigId="calmadmin",DbType=SqlSugar.DbType.SqlServer,InitKeyType=InitKeyType.Attribute,IsAutoCloseConnection=true,ConnectionString=new SqlConnectionStringBuilder(server){InitialCatalog=name}.ConnectionString});
 await new SystemDatabaseInitializer(scope,LoggerFactory.Create(_=>{}).CreateLogger<SystemDatabaseInitializer>()).InitializeAsync(default);
 var db=scope.GetConnectionScope("calmadmin"); var init=new WorkTaskInitializer(scope); await init.InitializeAsync(default);
 await WorkTaskMenus.SeedAsync(db); await WorkTaskMenus.SeedAsync(db);
 foreach(var key in new[]{"WorkTasksOverview","WorkTasksKeys"}) {
  Check(await db.Queryable<sys_menu>().AnyAsync(x=>x.MenuName==key),"A component registered: "+key);
  Check(await db.Queryable<SysApplicationMenuEntryEntity>().CountAsync(x=>x.AppCode=="B"&&x.PermissionKey==key)==1,"B default component added once: "+key);
 }
 foreach(var id in new[]{"worker1","worker2","leader","chief","team"}) await db.Insertable(new SysUserInfo {UserId=id,UserName=id,NickName=id,Password="test",Enabled=true,Roles="R_USER",CreateTime=DateTime.Now}).ExecuteCommandAsync();
 await db.Insertable(new[]{new WorkGroup{Id="dep1",Name="部门一"},new WorkGroup{Id="dep2",Name="部门二"},new WorkGroup{Id="group1",Name="第一组",ParentId="dep1"},new WorkGroup{Id="group2",Name="第二组",ParentId="dep2"}}).ExecuteCommandAsync();
 await db.Insertable(new[]{new WorkGrant{UserId="worker1",Role="worker",Scope="Self"},new WorkGrant{UserId="worker2",Role="worker",Scope="Self"},new WorkGrant{UserId="leader",Role="supervisor",Scope="GroupTree",TargetId="dep1"},new WorkGrant{UserId="team",Role="supervisor",Scope="Group",TargetId="group1"},new WorkGrant{UserId="chief",Role="supervisor",Scope="All"}}).ExecuteCommandAsync();
 await db.Insertable(new[]{new WorkTask{Id="t1",Title="一",GroupId="group1",AssigneeId="worker1",Status="InProgress",RevisionId="r",Blocked=true,CommittedDueAtUtc=DateTime.UtcNow.AddDays(-1)},new WorkTask{Id="t2",Title="二",GroupId="group2",AssigneeId="worker2",Status="Completed",RevisionId="r"},new WorkTask{Id="draft",Title="草稿",GroupId="group1",AssigneeId="worker1",Status="Draft"}}).ExecuteCommandAsync();
 var service=new WorkTaskService(scope,init,DispatchProxy.Create<ISystemApplicationService,Probe>(),Options.Create(new WorkTaskOptions()));
 WorkActor Actor(string id)=>new(new ApplicationAccessContext{AppCode="A",UserId=id});
 async Task<JsonElement> View(string id, WorkOverviewQuery? q=null)=>JsonSerializer.SerializeToElement(await service.OverviewAsync(Actor(id),q??new()),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
 Check((await View("worker1")).GetProperty("total").GetInt32()==1,"worker only sees own dispatched task");
 Check((await View("worker2")).GetProperty("total").GetInt32()==1,"other worker sees only own task");
 var one=await View("leader"); Check(one.GetProperty("total").GetInt32()==1 && one.GetProperty("members").GetArrayLength()==1,"department scope and organization data are filtered");
 Check(one.GetProperty("counts").GetProperty("overdue").GetInt32()==1 && one.GetProperty("counts").GetProperty("blocked").GetInt32()==1,"counts stay within permitted scope");
 Check((await View("team")).GetProperty("total").GetInt32()==1 && (await View("chief")).GetProperty("total").GetInt32()==2,"team and all scopes");
 Check((await View("leader",new(){GroupId="group2"})).GetProperty("total").GetInt32()==0,"filter cannot bypass scope");
 var detail=JsonSerializer.SerializeToElement(await service.DetailAsync(Actor("leader"),"t1"),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
 Check(!detail.GetProperty("canManage").GetBoolean()&&!detail.GetProperty("canReview").GetBoolean(),"viewer has no management or review rights");
 var leaderKey=JsonSerializer.SerializeToElement(await service.CreateKeyAsync(Actor("leader"),new WorkKeyRequest{Label="scoped test",Scopes="read,team_read,report",Days=1}),WorkTaskJson.Options);
 var leaderActor=await service.AuthenticateKeyAsync(leaderKey.GetProperty("secret").GetString()!);
 var empty=JsonSerializer.SerializeToElement(new{});
 var leaderList=await WorkTaskMcpTools.Call(service,leaderActor,"worktask.scope.list",empty);
 Check(leaderList.IsError!=true && leaderList.StructuredContent!.Value.GetProperty("total").GetInt32()==1,"MCP department leader sees only authorized scope");
 var foreignReport=JsonSerializer.SerializeToElement(new{taskId="t1",idempotencyKey="not-owner",text="cannot report for employee"});
 Check((await WorkTaskMcpTools.Call(service,leaderActor,"worktask.progress.report",foreignReport)).IsError==true,"MCP team viewing does not permit reporting as another worker");
 var outside=JsonSerializer.SerializeToElement(new{taskId="t2"});
 Check((await WorkTaskMcpTools.Call(service,leaderActor,"worktask.scope.get",outside)).IsError==true,"MCP rejects outside task detail");
 var inside=JsonSerializer.SerializeToElement(new{taskId="t1"});
 Check((await WorkTaskMcpTools.Call(service,leaderActor,"worktask.scope.get",inside)).IsError!=true,"MCP leader sees authorized details");
 Check((await WorkTaskMcpTools.Call(service,leaderActor,"worktask.my.get",inside)).IsError==true,"MCP my.get remains limited to own tasks");
 var chiefKey=JsonSerializer.SerializeToElement(await service.CreateKeyAsync(Actor("chief"),new WorkKeyRequest{Label="all",Scopes="read,team_read",Days=1}),WorkTaskJson.Options);
 var chiefActor=await service.AuthenticateKeyAsync(chiefKey.GetProperty("secret").GetString()!);
 Check((await WorkTaskMcpTools.Call(service,chiefActor,"worktask.scope.list",empty)).StructuredContent!.Value.GetProperty("total").GetInt32()==2,"MCP global leader sees both groups");
 var teamKey=JsonSerializer.SerializeToElement(await service.CreateKeyAsync(Actor("team"),new WorkKeyRequest{Label="team",Scopes="read,team_read",Days=1}),WorkTaskJson.Options);
 var teamActor=await service.AuthenticateKeyAsync(teamKey.GetProperty("secret").GetString()!);
 Check((await WorkTaskMcpTools.Call(service,teamActor,"worktask.scope.list",empty)).StructuredContent!.Value.GetProperty("total").GetInt32()==1,"MCP group leader sees only own group");
 var workerKey=JsonSerializer.SerializeToElement(await service.CreateKeyAsync(Actor("worker1"),new WorkKeyRequest{Label="old",Scopes="read",Days=1}),WorkTaskJson.Options);
 var workerActor=await service.AuthenticateKeyAsync(workerKey.GetProperty("secret").GetString()!);
 Check(!WorkTaskMcpTools.List(workerActor).Tools.Any(x=>x.Name=="worktask.scope.list"),"existing personal key does not gain team access");
 Check((await WorkTaskMcpTools.Call(service,workerActor,"worktask.my.get",outside)).IsError==true,"MCP worker cannot access another employee");
 try { await service.CreateKeyAsync(Actor("worker1"),new WorkKeyRequest{Label="invalid",Scopes="read,team_read",Days=1}); throw new Exception("worker scope escalation allowed"); } catch(UnauthorizedAccessException) { Check(true,"worker cannot grant team scope to own key"); }
 await db.Updateable<WorkGrant>().SetColumns(x=>x.Enabled==false).Where(x=>x.UserId=="leader" && x.Role=="supervisor").ExecuteCommandAsync();
 Check((await WorkTaskMcpTools.Call(service,leaderActor,"worktask.scope.get",inside)).IsError==true,"scope revoke immediately removes access");
 await db.Updateable<WorkGrant>().SetColumns(x=>x.Enabled==true).Where(x=>x.UserId=="leader" && x.Role=="supervisor").ExecuteCommandAsync();
 await db.Updateable<WorkGroup>().SetColumns(x=>x.ParentId=="dep2").Where(x=>x.Id=="group1").ExecuteCommandAsync();
 Check((await View("leader")).GetProperty("total").GetInt32()==0 && (await View("team")).GetProperty("total").GetInt32()==1,"history follows the group when moved");
 var permission=new HashSet<string>(); await WorkTaskAccessProjection.ApplyAsync(db,permission,"worker1","B",false);
 // A and B authorizations are intentionally independent.
 Check(!permission.Contains("WorkTasksKeys"),"B does not inherit A business authorization");
 await db.Insertable(new WorkGrant{AppCode="B",UserId="worker1",Role="worker",Scope="Self"}).ExecuteCommandAsync();
 await WorkTaskAccessProjection.ApplyAsync(db,permission,"worker1","B",false);
 Check(permission.Contains("WorkTasksKeys")&&permission.Contains("WorkTasksOverview"),"B worker gets MCP and overview menu access");

 foreach(var uid in new[]{"leader","worker1","chief"}) await db.Insertable(new SysUserApplicationEntity{AppCode="A",UserId=uid,Enabled=true,PermissionsJson="[]",McpScopesJson="[]",UpdateTime=DateTime.Now}).ExecuteCommandAsync();
 await db.Insertable(new WorkMember{AppCode="A",GroupId="group1",UserId="worker1"}).ExecuteCommandAsync();
 await db.Insertable(new WorkGrant{AppCode="A",UserId="leader",Role="dispatcher",Scope="Group",TargetId="group1"}).ExecuteCommandAsync();
 var dispatchKey=JsonSerializer.SerializeToElement(await service.CreateKeyAsync(Actor("leader"),new WorkKeyRequest{Label="dispatch test",Scopes="read,dispatch",Days=1}),WorkTaskJson.Options);
 var dispatcher=await service.AuthenticateKeyAsync(dispatchKey.GetProperty("secret").GetString()!);
 Check(WorkTaskMcpTools.List(dispatcher).Tools.Any(x=>x.Name=="worktask.dispatch.submit"),"dispatch MCP tool enabled for explicitly scoped key");
 Check(!WorkTaskMcpTools.List(leaderActor).Tools.Any(x=>x.Name=="worktask.dispatch.submit"),"existing key does not gain dispatch permission");
 var groupsResult=await WorkTaskMcpTools.Call(service,dispatcher,"worktask.dispatch.groups",empty);
 Check(groupsResult.IsError!=true && groupsResult.StructuredContent!.Value.EnumerateArray().Any(x=>x.GetProperty("id").GetString()=="group1"),"authorized dispatch group visible");
 var candidateArgs=JsonSerializer.SerializeToElement(new{purpose="assignee",groupId="group1"});
 var candidateResult=await WorkTaskMcpTools.Call(service,dispatcher,"worktask.dispatch.candidates",candidateArgs);
 Check(candidateResult.IsError!=true && candidateResult.StructuredContent!.Value.GetProperty("records").EnumerateArray().Any(x=>x.GetProperty("value").GetString()=="worker1"),"eligible assigned worker found");
 var draftArgs=JsonSerializer.SerializeToElement(new{idempotencyKey="draft-key",title="MCP 新任务",requirements="真实要求",acceptanceCriteria="文字结果",groupId="group1",assigneeId="worker1",requiresReview=false});
 var savedDraft=await WorkTaskMcpTools.Call(service,dispatcher,"worktask.dispatch.draft",draftArgs);
 Check(savedDraft.IsError!=true,"MCP creates task draft");
 var taskId=savedDraft.StructuredContent!.Value.GetProperty("taskId").GetString()!;
 var version=savedDraft.StructuredContent.Value.GetProperty("version").GetInt32();
 var retryDraft=await WorkTaskMcpTools.Call(service,dispatcher,"worktask.dispatch.draft",draftArgs);
 Check(retryDraft.IsError!=true && retryDraft.StructuredContent!.Value.GetProperty("taskId").GetString()==taskId,"draft retry does not duplicate task");
 Check((await WorkTaskMcpTools.Call(service,dispatcher,"worktask.dispatch.get",JsonSerializer.SerializeToElement(new{taskId}))).IsError!=true,"dispatcher can preview own draft");
 var unconfirmed=JsonSerializer.SerializeToElement(new{taskId,idempotencyKey="dispatch-key",expectedVersion=version,confirmed=false});
 Check((await WorkTaskMcpTools.Call(service,dispatcher,"worktask.dispatch.submit",unconfirmed)).IsError==true && (await db.Queryable<WorkTask>().InSingleAsync(taskId)).Status=="Draft","unconfirmed dispatch does not create employee task");
 var submitArgs=JsonSerializer.SerializeToElement(new{taskId,idempotencyKey="dispatch-key",expectedVersion=version,confirmed=true});
 var sent=await WorkTaskMcpTools.Call(service,dispatcher,"worktask.dispatch.submit",submitArgs);
 Check(sent.IsError!=true && (await db.Queryable<WorkTask>().InSingleAsync(taskId)).Status=="PendingAssessment","confirmed dispatch creates employee task");
 Check((await WorkTaskMcpTools.Call(service,dispatcher,"worktask.dispatch.submit",submitArgs)).IsError!=true,"dispatch retries return same result");
 var forbidden=JsonSerializer.SerializeToElement(new{idempotencyKey="bad",title="越权",groupId="group2",assigneeId="worker2"});
 Check((await WorkTaskMcpTools.Call(service,dispatcher,"worktask.dispatch.draft",forbidden)).IsError==true,"outside group dispatch rejected");
 try { await service.CreateKeyAsync(Actor("worker1"),new WorkKeyRequest{Label="no",Scopes="read,dispatch",Days=1}); throw new Exception("worker can create dispatch key"); } catch(UnauthorizedAccessException){Check(true,"worker cannot grant dispatch permission to key");}
 await db.Updateable<WorkGrant>().SetColumns(x=>x.Enabled==false).Where(x=>x.UserId=="leader" && x.Role=="dispatcher").ExecuteCommandAsync();
 Check((await WorkTaskMcpTools.Call(service,dispatcher,"worktask.dispatch.draft",JsonSerializer.SerializeToElement(new{idempotencyKey="revoked",title="撤权",groupId="group1",assigneeId="worker1"}))).IsError==true,"revoked dispatch role immediately blocks new drafts");
 Console.WriteLine("PASS work overview focused checks");
} finally { SqlConnection.ClearAllPools(); await new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]",master).ExecuteNonQueryAsync(); }
static void Check(bool value,string message){if(!value)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);}
public class Probe:DispatchProxy { protected override object? Invoke(MethodInfo? method,object?[]? args) => method?.Name == "GetChannelAccessAsync" ? Task.FromResult(new ApplicationAccessContext { UserId=(string)args![0]!, AppCode=(string)args[1]! }) : throw new NotSupportedException(); }

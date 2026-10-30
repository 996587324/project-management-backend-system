using System.Reflection;
using System.Text.Json;
using Interface;
using Microsoft.Data.SqlClient;
using Model.HourlyProduction;
using Model.SystemApplications;
using Service;
using SqlSugar;
var name="CodexLineRemoval_"+Guid.NewGuid().ToString("N");
var server=@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false;Connect Timeout=15";
await using var master=new SqlConnection(new SqlConnectionStringBuilder(server){InitialCatalog="master"}.ConnectionString);await master.OpenAsync();await new SqlCommand($"CREATE DATABASE [{name}]",master).ExecuteNonQueryAsync();
SqlSugarScope? scope=null;
try {
 scope=new(new ConnectionConfig{ConfigId="calmadmin",DbType=SqlSugar.DbType.SqlServer,InitKeyType=InitKeyType.Attribute,IsAutoCloseConnection=true,ConnectionString=new SqlConnectionStringBuilder(server){InitialCatalog=name}.ConnectionString});
 await new SystemDatabaseInitializer(scope,LoggerFactory.Create(_=>{}).CreateLogger<SystemDatabaseInitializer>()).InitializeAsync(default);
 var catalog=new HourlyProductionCatalog();await new HourlyProductionInitializer(scope,catalog).InitializeAsync(default);
 var db=scope.GetConnectionScope("calmadmin");var service=new HourlyProductionService(scope,catalog,Mock<ISystemApplicationService>(),Mock<IEmailPushService>(),Mock<IWeChatPushQueueService>(),Mock<IAgentModelClient>());
 var access=new ApplicationAccessContext{AppCode="A",UserId="tester",AllResources=true,Permissions=new HashSet<string>{"HourlyProductionConfig"}};


 HourlyLineDefinition Line(string code)=>new(){Code=code,Name=code,SummaryProcessCode="P1",Processes=[new(){Code="P1",Name="工序",RowCode="row"}]};
 HourlyBoardDefinition Copy(HourlyBoardDefinition b)=>JsonSerializer.Deserialize<HourlyBoardDefinition>(JsonSerializer.Serialize(b))!;
 var b=await service.SaveAsync(access,new(){Code="removal",Lines=[Line("A"),Line("B")],Layout=new(){Mode="Compare",Groups=[["A","B"]]}});
 var processKey=b.Lines[0].Processes[0].InstanceId;
 await db.Insertable(new HourlyProductionBucketEntity{BoardCode=b.Code,ProcessKey=processKey,SlotStart=HourlyProductionRules.Now.Date.AddHours(8),Output=25,UpdatedAt=HourlyProductionRules.Now}).ExecuteCommandAsync();
 var stale=Copy(b);b.Lines.RemoveAt(0);
 await Reject(()=>service.SaveAsync(access,b),"dangling comparison references rejected");
 b.Layout=new(){Mode="Single",Groups=[["B"]]};b=await service.SaveAsync(access,b);
 Check(b.Lines.Count==1&&b.RemovedLines.Count==1&&b.RemovedLines[0].Definition.Code=="A"&&b.RemovedLines[0].RemovedBy=="tester","removed line archived with operator");
 Check(await db.Queryable<HourlyProductionBucketEntity>().AnyAsync(x=>x.BoardCode==b.Code&&x.ProcessKey==processKey&&x.Output==25),"historical output preserved");
 Check(!await db.Queryable<HourlyProductionProcessEntity>().AnyAsync(x=>x.BoardCode==b.Code&&x.ProcessKey==processKey&&x.Active),"removed line process no longer active");
 Check(catalog.Boards.Single(x=>x.Code==b.Code).Lines.All(x=>x.Code!="A"),"live ingestion catalog excludes removed line");
 var runtime=await service.RuntimeAsync(access,b.Code);Check(runtime.Lines.Count==1&&runtime.Lines[0].Code=="B","runtime excludes removed line");
 var readd=Copy(b);readd.RemovedLines=[];readd.Lines.Add(Line("a"));await Reject(()=>service.SaveAsync(access,readd),"retired code cannot be reused or archive spoofed");
 var empty=Copy(b);empty.Lines=[];empty.Layout=null;await Reject(()=>service.SaveAsync(access,empty),"last line cannot be removed");
 stale.Layout=null;stale.Lines.RemoveAt(0);await Reject(()=>service.SaveAsync(access,stale),"stale removal rejected");
 var next=Copy(b);next.Lines.Add(Line("C"));next=await service.SaveAsync(access,next);Check(next.RemovedLines.Count==1,"archive retained on subsequent saves");
 Check((await service.ListAsync(access)).Single().RemovedLines[0].Definition.Processes[0].InstanceId==processKey,"original process identity retained on reload");
 Console.WriteLine("PASS focused line removal SQL tests");
} finally {scope?.Dispose();SqlConnection.ClearAllPools();await new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]",master).ExecuteNonQueryAsync();}
static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
static async Task Reject(Func<Task<HourlyBoardDefinition>> action,string message){try{await action();}catch(Exception e)when(e is InvalidOperationException or ArgumentException or UnauthorizedAccessException){Check(true,message);return;}throw new Exception("unexpected success: "+message);}
static T Mock<T>()where T:class=>DispatchProxy.Create<T,Probe>();
public class Probe:DispatchProxy{protected override object? Invoke(MethodInfo? m,object?[]? args)=>throw new NotSupportedException();}

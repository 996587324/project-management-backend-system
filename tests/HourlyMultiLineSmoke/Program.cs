using System.Reflection;
using System.Text.Json;
using Interface;
using Microsoft.Data.SqlClient;
using Model.HourlyProduction;
using Model.SystemApplications;
using Service;
using SqlSugar;
var name="CodexMultiLine_"+Guid.NewGuid().ToString("N");
var server=@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false;Connect Timeout=15";
await using var master=new SqlConnection(new SqlConnectionStringBuilder(server){InitialCatalog="master"}.ConnectionString);await master.OpenAsync();await new SqlCommand($"CREATE DATABASE [{name}]",master).ExecuteNonQueryAsync();
SqlSugarScope? scope=null;
try {
 scope=new(new ConnectionConfig{ConfigId="calmadmin",DbType=SqlSugar.DbType.SqlServer,InitKeyType=InitKeyType.Attribute,IsAutoCloseConnection=true,ConnectionString=new SqlConnectionStringBuilder(server){InitialCatalog=name}.ConnectionString});
 await new SystemDatabaseInitializer(scope,LoggerFactory.Create(_=>{}).CreateLogger<SystemDatabaseInitializer>()).InitializeAsync(default);
 var catalog=new HourlyProductionCatalog();await new HourlyProductionInitializer(scope,catalog).InitializeAsync(default);
 var db=scope.GetConnectionScope("calmadmin");var service=new HourlyProductionService(scope,catalog,Mock<ISystemApplicationService>(),Mock<IEmailPushService>(),Mock<IWeChatPushQueueService>(),Mock<IAgentModelClient>());
 var access=new ApplicationAccessContext{AppCode="A",UserId="tester",AllResources=true,Permissions=new HashSet<string>{"HourlyProductionConfig"}};

 HourlyLineDefinition Line(string code)=>new(){Code=code,Name=code+"生产线",SummaryProcessCode="P1",Processes=[new(){Code="P1",Name="工序",RowCode="row"}]};
 var b=new HourlyBoardDefinition{Code="multi",Lines=[Line("A"),Line("B"),Line("C")],Layout=new(){Mode="Rotate",Groups=[["A","C"],["B","C"]]}};
 b=await service.SaveAsync(access,b);
 Check(b.Lines.Count==3,"three lines saved");
 var runtime=await service.RuntimeAsync(access,"multi");
 Check(runtime.Lines.Count==3 && runtime.Layout!.Groups[0].SequenceEqual(new[]{"A","C"}),"runtime preserves all statistics and comparison order");
 b.Lines[0].Name="组装一线";b.Lines.Add(Line("D"));b=await service.SaveAsync(access,b);
 Check(b.Lines.Count==4&&b.Lines[0].Name=="组装一线","add line to existing board and rename");
 var before=b.Version;
 b.Layout!.Groups=[["A","A"]];await Reject(()=>service.SaveAsync(access,b),"same line comparison denied");
 b.Layout.Groups=[["A","missing"]];await Reject(()=>service.SaveAsync(access,b),"unknown comparison line denied");
 b.Layout.Mode="Single";b.Layout.Groups=[["C"]];b=await service.SaveAsync(access,b);
 Check((await service.RuntimeAsync(access,"multi")).Layout!.Groups[0][0]=="C","single line selection persisted");
 var original=(await service.ListAsync(access)).Single();original.Layout=null;original.Lines.Add(Line("a"));await Reject(()=>service.SaveAsync(access,original),"duplicate line codes ignore case");
 var single=new HourlyBoardDefinition{Code="one",Lines=[Line("X")]};single=await service.SaveAsync(access,single);Check(single.Lines.Count==1,"one line supported without layout");
 var legacy=new HourlyBoardDefinition{Code="legacy",Lines=[Line("A"),Line("B")]};legacy=await service.SaveAsync(access,legacy);Check(legacy.Layout==null,"old two-line boards remain compatible");
 Check((await db.Queryable<HourlyProductionBoardEntity>().InSingleAsync("multi")).Version>before,"real SQL version persistence");
 Console.WriteLine("PASS focused multiline layout SQL tests");
} finally {scope?.Dispose();SqlConnection.ClearAllPools();await new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]",master).ExecuteNonQueryAsync();}
static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
static async Task Reject(Func<Task<HourlyBoardDefinition>> action,string message){try{await action();}catch(Exception e)when(e is InvalidOperationException or ArgumentException or UnauthorizedAccessException){Check(true,message);return;}throw new Exception("unexpected success: "+message);}
static T Mock<T>()where T:class=>DispatchProxy.Create<T,Probe>();
public class Probe:DispatchProxy{protected override object? Invoke(MethodInfo? m,object?[]? args)=>throw new NotSupportedException();}

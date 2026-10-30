using System.Reflection;
using System.Text.Json;
using Interface;
using Microsoft.Data.SqlClient;
using Model.HourlyProduction;
using Model.SystemApplications;
using Service;
using SqlSugar;
var name="CodexShiftCancel_"+Guid.NewGuid().ToString("N");
var server=@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false;Connect Timeout=15";
await using var master=new SqlConnection(new SqlConnectionStringBuilder(server){InitialCatalog="master"}.ConnectionString);await master.OpenAsync();await new SqlCommand($"CREATE DATABASE [{name}]",master).ExecuteNonQueryAsync();
SqlSugarScope? scope=null;
try {
 scope=new(new ConnectionConfig{ConfigId="calmadmin",DbType=SqlSugar.DbType.SqlServer,InitKeyType=InitKeyType.Attribute,IsAutoCloseConnection=true,ConnectionString=new SqlConnectionStringBuilder(server){InitialCatalog=name}.ConnectionString});
 await new SystemDatabaseInitializer(scope,LoggerFactory.Create(_=>{}).CreateLogger<SystemDatabaseInitializer>()).InitializeAsync(default);
 var catalog=new HourlyProductionCatalog();await new HourlyProductionInitializer(scope,catalog).InitializeAsync(default);
 var db=scope.GetConnectionScope("calmadmin");var service=new HourlyProductionService(scope,catalog,Mock<ISystemApplicationService>(),Mock<IEmailPushService>(),Mock<IWeChatPushQueueService>(),Mock<IAgentModelClient>());
 var access=new ApplicationAccessContext{AppCode="A",UserId="tester",AllResources=true,Permissions=new HashSet<string>{"HourlyProductionConfig"}};
 var t=HourlyProductionRules.Now.Date.AddDays(2).AddHours(20);
 HourlyBoardDefinition NewBoard()=>new(){Code="cancel",Lines=new[]{"L1","L2"}.Select(x=>new HourlyLineDefinition{Code=x,Name=x,SummaryProcessCode="P1",Processes=[new(){Code="P1",Name="process",RowCode="final"}]}).ToList()};
 HourlyBoardDefinition Copy(HourlyBoardDefinition b)=>JsonSerializer.Deserialize<HourlyBoardDefinition>(JsonSerializer.Serialize(b))!;
 var b=NewBoard();b.ShiftChange=new(){EffectiveAt=t,DayStart="07:00",NightStart="20:00"};b=await service.SaveAsync(access,b);
 b.ShiftChange=new(){EffectiveAt=t.AddHours(11),DayStart="07:00",NightStart="21:00"};b=await service.SaveAsync(access,b);
 var invalid=Copy(b);invalid.CancelShiftEffectiveAt=t;
 await Reject(()=>service.SaveAsync(access,invalid),"dependent later shift prevents cancellation");
 Check((await service.ListAsync(access)).Single().ShiftHistory.Count==2,"failed cancellation leaves database unchanged");
 var stale=Copy(b);b.CancelShiftEffectiveAt=t.AddHours(11);b=await service.SaveAsync(access,b);
 Check(b.ShiftHistory.Count==1&&b.CancelledShifts.Count==1&&b.CancelledShifts[0].CancelledBy=="tester","future version removed and audit persisted");
 stale.CancelShiftEffectiveAt=t;await Reject(()=>service.SaveAsync(access,stale),"stale config rejected");
 b.CancelShiftEffectiveAt=t;b=await service.SaveAsync(access,b);
 Check(b.ShiftHistory.Count==0&&b.CancelledShifts.Count==2&&b.CancelShiftEffectiveAt==null,"cancel first pending version and clear request");
 Check(HourlyProductionRules.Plan(b,t.AddDays(1))==null,"future time falls back to legacy configuration");
 var persisted=(await service.ListAsync(access)).Single();Check(persisted.CancelledShifts.Count==2,"audit survives database reload");
 var historic=new HourlyShiftPlan{EffectiveAt=HourlyProductionRules.Now.AddMinutes(-1)};
 persisted.ShiftHistory.Add(historic);var persistedJson=JsonSerializer.Serialize(persisted);await db.Updateable<HourlyProductionBoardEntity>().SetColumns(x=>x.DefinitionJson==persistedJson).Where(x=>x.Code=="cancel").ExecuteCommandAsync();
 persisted.CancelShiftEffectiveAt=historic.EffectiveAt;await Reject(()=>service.SaveAsync(access,persisted),"already effective version protected");
 var boundary=new HourlyBoardDefinition{ShiftHistory=[new(){EffectiveAt=t}]};
 try{HourlyProductionRules.CancelPendingShift(boundary,t,t);throw new Exception("boundary accepted");}catch(InvalidOperationException){Check(boundary.ShiftHistory.Count==1,"exact effective time is protected without mutation");}
 var wrong=new ApplicationAccessContext{AppCode="B",UserId="other",AllResources=true,Permissions=new HashSet<string>{"HourlyProductionConfig"}};
 await Reject(()=>service.SaveAsync(wrong,persisted),"cross application cancellation denied");
 Console.WriteLine("PASS focused cancellation SQL tests");
} finally {scope?.Dispose();SqlConnection.ClearAllPools();await new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]",master).ExecuteNonQueryAsync();}
static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
static async Task Reject(Func<Task<HourlyBoardDefinition>> action,string message){try{await action();}catch(Exception e)when(e is InvalidOperationException or ArgumentException or UnauthorizedAccessException){Check(true,message);return;}throw new Exception("unexpected success: "+message);}
static T Mock<T>()where T:class=>DispatchProxy.Create<T,Probe>();
public class Probe:DispatchProxy{protected override object? Invoke(MethodInfo? m,object?[]? args)=>throw new NotSupportedException();}

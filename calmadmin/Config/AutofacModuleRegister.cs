using Autofac;
using System.Reflection;

namespace 上传MES.Config
{
    public class AutofacModuleRegister:Autofac.Module
    {
        //注册接口和实现之间的关系
        protected override void Load(ContainerBuilder builder)
        {
            Assembly interfaceAssembly = Assembly.Load("Interface");
            Assembly serviceAssembly = Assembly.Load("Service");
            builder.RegisterAssemblyTypes(interfaceAssembly, serviceAssembly)
                .Except<Service.EdgeLocalDatabaseService>()
                .Except<Service.AgentModelDatabaseInitializer>()
                .Except<Service.AgentSecretProtector>()
                .Except<Service.EmailPushDatabaseInitializer>()
                .Except<Service.EmailSecretProtector>()
                .Except<Service.WeChatPushQueueInitializer>()
                .Except<Service.HourlyProductionInitializer>()
                .AsImplementedInterfaces();

            // SQLite 必须由唯一服务实例持有连接和访问锁，禁止采集、上传、心跳各建连接。
            builder.RegisterType<Service.EdgeLocalDatabaseService>()
                .As<Interface.IEdgeLocalDatabaseService>()
                .SingleInstance();
        }
    }
}

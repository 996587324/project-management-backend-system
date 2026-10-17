using AutoMapper;
using Model.User;
using Model;
using System.Data;
using Model.Entitys;
using Model.Menu;
using Newtonsoft.Json;
using Model.Dto;
using Model.EdgeGateway;
using Model.Agent;
using Model.Agent.ToolArguments;
using calmadmin.Config;

namespace 上传MES.Config
{
    /// <summary>
    /// Dto的映射配置
    /// </summary>
    public class AutoMapperConfigs : Profile
    {
        public AutoMapperConfigs()
        {
            //用户
            CreateMap<UserAdd, UserRes>().ReverseMap();
            CreateMap<UserEdit, UserRes>().ReverseMap();
          
            CreateMap<Users, UserRes>();
            CreateMap<UserAdd, Users>();
            CreateMap<UserEdit, Users>();

            // 1. 核心映射：SysMenu → FrontMenuDto
            CreateMap<sys_menu, FrontMenuDto>()
                // 普通字段映射（名称不同的手动指定，名称一致的自动映射）
                .ForMember(dest => dest.id, opt => opt.MapFrom(src => src.MenuId))
                .ForMember(dest => dest.name, opt => opt.MapFrom(src => src.MenuName))
                .ForMember(dest => dest.path, opt => opt.MapFrom(src => src.Path))
                .ForMember(dest => dest.component, opt => opt.MapFrom(src => src.Component))
                // 嵌套对象：Meta字段映射
                .ForMember(dest => dest.meta, opt => opt.MapFrom(src => src))
                // 子菜单递归映射
                .ForMember(dest => dest.children, opt => opt.MapFrom(src => src.Children));

            // 2. 嵌套映射：SysMenu → FrontMenuMetaDto（Meta字段单独映射）
            CreateMap<sys_menu, FrontMenuMetaDto>()
                .ForMember(dest => dest.isEnable, opt => opt.MapFrom(src => src.Status == true))
                .ForMember(dest => dest.sort, opt => opt.MapFrom(src => src.Sort ?? 0))
                .ForMember(dest => dest.permissionKey, opt => opt.MapFrom(src => src.MenuName))
                .ForMember(dest => dest.title, opt => opt.MapFrom(src => src.MetaTitle))
                .ForMember(dest => dest.icon, opt => opt.MapFrom(src => src.MetaIcon))
                .ForMember(dest => dest.keepAlive, opt => opt.MapFrom(src => src.MetaKeepAlive))
                .ForMember(dest => dest.fixedTab, opt => opt.MapFrom(src => src.MetaFixedTab))
                .ForMember(dest => dest.isHide, opt => opt.MapFrom(src => src.MetaIsHide))
                .ForMember(dest => dest.isHideTab, opt => opt.MapFrom(src => src.MetaIsHideTab))
                .ForMember(dest => dest.isFullPage, opt => opt.MapFrom(src => src.MetaIsFullPage))
                    // 自定义转换：逗号分隔的字符串 → List<string>（角色）
                .ForMember(dest => dest.roles, opt => opt.MapFrom(src =>
                MenuFieldConverter.ConvertStringToList(src.MetaRoles)))
                // 自定义转换：JSON字符串 → List<FrontMenuAuthItemDto>（按钮权限）
                .ForMember(dest => dest.authList, opt => opt.MapFrom(src =>
                    string.IsNullOrEmpty(src.MetaAuthList)
                        ? new List<FrontMenuAuthItemDto>()
                        : JsonConvert.DeserializeObject<List<FrontMenuAuthItemDto>>(src.MetaAuthList)));

            // 3. 按钮权限映射（如果有数据库实体的话，这里直接匹配字段）
            CreateMap<MenuAuthItem, FrontMenuAuthItemDto>()
                .ForMember(dest => dest.title, opt => opt.MapFrom(src => src.Title))
                .ForMember(dest => dest.authMark, opt => opt.MapFrom(src => src.AuthMark));



            // 商品管理Dto → Entity
            CreateMap<BusinessBillCreateDto, BusinessBill>();

            CreateMap<BusinessBillUpdateDto, BusinessBill>();

            // Agent 通用核心映射：模块配置、工具配置、会话和执行计划统一由 AutoMapper 转换。
            CreateMap<AgentModuleSeed, AgentDefinitionEntity>(MemberList.None)
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<AgentToolSeed, AgentToolRegistryEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<AgentConversationEntity, AgentConversationDto>(MemberList.None);
            CreateMap<AgentMessageEntity, AgentMessageDto>(MemberList.None)
                .ForMember(dest => dest.Content, opt => opt.MapFrom(src => src.Content ?? string.Empty));
            CreateMap<AgentToolRegistryEntity, AgentToolDefinitionDto>(MemberList.None)
                .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.ToolName))
                .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description ?? string.Empty))
                .ForMember(dest => dest.Category, opt => opt.MapFrom(src => src.ServiceName))
                .ForMember(dest => dest.Route, opt => opt.MapFrom(src => src.Route ?? string.Empty))
                .ForMember(dest => dest.PermissionKey, opt => opt.MapFrom(src => src.PermissionKey ?? string.Empty))
                .ForMember(dest => dest.Examples, opt => opt.MapFrom(src => src.Examples ?? string.Empty));
            CreateMap<AgentExecutionPlanEntity, AgentToolCallDto>(MemberList.None)
                .ForMember(dest => dest.RequestJson, opt => opt.MapFrom(src => src.ArgumentsJson))
                .ForMember(dest => dest.ResponseJson, opt => opt.MapFrom(src => src.ResultJson ?? string.Empty))
                .ForMember(dest => dest.Status, opt => opt.MapFrom(src =>
                    src.Status == "PendingConfirmation" ? "PendingConfirm" :
                    src.Status == "Succeeded" ? "Success" :
                    src.Status == "PartiallySucceeded" ? "PartialSuccess" :
                    src.Status == "InvalidArguments" ? "Invalid" :
                    src.Status == "ToolNotFound" || src.Status == "Disabled" || src.Status == "ArgumentsChanged"
                        ? "Blocked"
                        : src.Status))
                .ForMember(dest => dest.RequiresConfirmation, opt => opt.MapFrom(src =>
                    src.RequiresConfirmation && src.Status == "PendingConfirmation"))
                // 历史实体的 ConfirmTime 有初始化值；必须存在真实确认人，不能仅凭时间认定已确认。
                .ForMember(dest => dest.Confirmed, opt => opt.MapFrom(src =>
                    src.ConfirmTime.HasValue && !string.IsNullOrWhiteSpace(src.ConfirmedBy)));

            // Agent 模型平台映射：密钥字段由专用保护服务处理，不允许 AutoMapper 复制。
            CreateMap<AgentModelProviderSaveDto, AgentModelProviderEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.OwnerUserId, opt => opt.Ignore())
                .ForMember(dest => dest.ApiKeyCipherText, opt => opt.Ignore())
                .ForMember(dest => dest.ApiKeyMask, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<AgentModelProviderEntity, AgentModelProviderDto>(MemberList.None)
                .ForMember(dest => dest.HasApiKey, opt => opt.Ignore());
            CreateMap<AgentModelBindingSaveDto, AgentModelBindingEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.OwnerUserId, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<AgentModelBindingEntity, AgentModelBindingDto>(MemberList.None)
                .ForMember(dest => dest.AgentName, opt => opt.Ignore())
                .ForMember(dest => dest.ProviderName, opt => opt.Ignore());
            CreateMap<AgentModelInvocationLogWriteDto, AgentModelInvocationLogEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore());

            // 边缘 Agent 的强类型参数映射到现有 EdgeGateway 服务查询 DTO。
            CreateMap<EdgeDeviceListArguments, EdgeDeviceQueryDto>(MemberList.None);
            CreateMap<EdgeConnectionListArguments, EdgeDeviceQueryDto>(MemberList.None)
                .ForMember(dest => dest.Keyword, opt => opt.MapFrom(src => src.DeviceCode))
                .ForMember(dest => dest.OnlineStatus, opt => opt.Ignore());
            CreateMap<EdgeQueueListArguments, EdgeQueueQueryDto>(MemberList.None);
            CreateMap<EdgeUploadRecordListArguments, EdgeUploadRecordQueryDto>(MemberList.None);
            CreateMap<EdgeEventListArguments, EdgeEventQueryDto>(MemberList.None);
            CreateMap<EdgeWebSocketSourceOutputDto, EdgeAgentWebSocketSourceSummaryDto>(MemberList.None)
                .ForMember(dest => dest.SubscriptionDeviceCount, opt => opt.MapFrom(src =>
                    src.SubscriptionDevices.Count))
                .ForMember(dest => dest.SubscriptionParameterCount, opt => opt.MapFrom(src =>
                    src.SubscriptionDevices.Sum(device => device.Parameters.Count)));
            CreateMap<EdgeBMappingOutputDto, EdgeAgentBMappingSummaryDto>(MemberList.None);

            // EdgeGateway 中心端映射：负责接口 DTO、SQL Server 实体以及下发给 Worker 的数据源配置转换。
            CreateMap<EdgeDeviceSaveDto, EdgeDeviceEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DeviceCode, opt => opt.MapFrom(src => src.DeviceCode.Trim()))
                .ForMember(dest => dest.DeviceName, opt => opt.MapFrom(src => src.DeviceName.Trim()))
                .ForMember(dest => dest.SiteName, opt => opt.MapFrom(src => src.SiteName == null ? null : src.SiteName.Trim()))
                .ForMember(dest => dest.DeviceType, opt => opt.MapFrom(src => src.DeviceType == null ? null : src.DeviceType.Trim()))
                .ForMember(dest => dest.GroupName, opt => opt.MapFrom(src => src.GroupName == null ? null : src.GroupName.Trim()))
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeHeartbeatDto, EdgeDeviceEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DeviceCode, opt => opt.MapFrom(src => src.DeviceCode.Trim()))
                .ForMember(dest => dest.DeviceName, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.DeviceName) ? src.DeviceCode.Trim() : src.DeviceName.Trim()))
                .ForMember(dest => dest.DeviceType, opt => opt.MapFrom(_ => "EdgeGateway"))
                .ForMember(dest => dest.SiteName, opt => opt.Ignore())
                .ForMember(dest => dest.GroupName, opt => opt.Ignore())
                .ForMember(dest => dest.Enabled, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeDeviceEntity, EdgeDeviceOutputDto>(MemberList.None);
            CreateMap<EdgeDeviceStatusEntity, EdgeDeviceOutputDto>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeHeartbeatDto, EdgeDeviceStatusEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DeviceId, opt => opt.Ignore())
                .ForMember(dest => dest.LastHeartbeat, opt => opt.MapFrom(_ => DateTime.Now))
                .ForMember(dest => dest.UpdateTime, opt => opt.MapFrom(_ => DateTime.Now));

            CreateMap<EdgeWebSocketSourceSaveDto, EdgeWebSocketSourceEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DeviceId, opt => opt.Ignore())
                .ForMember(dest => dest.SourceId, opt => opt.MapFrom(src => src.SourceId.Trim()))
                .ForMember(dest => dest.SourceName, opt => opt.MapFrom(src => src.SourceName.Trim()))
                .ForMember(dest => dest.Endpoint, opt => opt.MapFrom(src => src.Endpoint.Trim()))
                .ForMember(dest => dest.SubscriptionPayload, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.SubscriptionPayload) ? null : src.SubscriptionPayload.Trim()))
                .ForMember(dest => dest.HeadersJson, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.HeadersJson) ? null : src.HeadersJson.Trim()))
                .ForMember(dest => dest.DataType, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.DataType) ? "Telemetry" : src.DataType.Trim()))
                .ForMember(dest => dest.Version, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeWebSocketSourceEntity, EdgeWebSocketSourceOutputDto>(MemberList.None)
                .ForMember(dest => dest.DataOnline, opt => opt.Ignore())
                .ForMember(dest => dest.DataStatus, opt => opt.Ignore())
                .ForMember(dest => dest.LastDataTime, opt => opt.Ignore());

            CreateMap<EdgeWebSocketSubscriptionDeviceSaveDto, EdgeWebSocketSubscriptionDeviceEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.WebSocketSourceId, opt => opt.Ignore())
                .ForMember(dest => dest.DeviceCode, opt => opt.MapFrom(src => src.DeviceCode.Trim()))
                .ForMember(dest => dest.DeviceName, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.DeviceName) ? null : src.DeviceName.Trim()))
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeWebSocketSubscriptionParameterSaveDto, EdgeWebSocketSubscriptionParameterEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.SubscriptionDeviceId, opt => opt.Ignore())
                .ForMember(dest => dest.ParameterName, opt => opt.MapFrom(src => src.ParameterName.Trim()))
                .ForMember(dest => dest.ParameterNameCn, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.ParameterNameCn) ? null : src.ParameterNameCn.Trim()))
                .ForMember(dest => dest.DataType, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.DataType) ? null : src.DataType.Trim()))
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeWebSocketSubscriptionDeviceEntity, EdgeWebSocketSubscriptionDeviceOutputDto>(MemberList.None)
                .ForMember(dest => dest.Parameters, opt => opt.Ignore());
            CreateMap<EdgeWebSocketSubscriptionParameterEntity, EdgeWebSocketSubscriptionParameterOutputDto>(MemberList.None);

            CreateMap<EdgeWebSocketSourceEntity, SourceOptions>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.SourceId))
                .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.SourceName))
                .ForMember(dest => dest.Protocol, opt => opt.MapFrom(_ => "WebSocket"))
                .ForMember(dest => dest.RuntimeVersion, opt => opt.MapFrom(src => src.Version))
                .ForMember(dest => dest.SubscriptionDevices, opt => opt.Ignore())
                .ForMember(dest => dest.Topic, opt => opt.Ignore())
                .ForMember(dest => dest.ClientId, opt => opt.Ignore())
                .ForMember(dest => dest.Username, opt => opt.Ignore())
                .ForMember(dest => dest.Password, opt => opt.Ignore());

            CreateMap<EdgeDeviceConfigSaveDto, EdgeDeviceConfigEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DeviceId, opt => opt.Ignore())
                .ForMember(dest => dest.Version, opt => opt.Ignore())
                .ForMember(dest => dest.ConfigName, opt => opt.MapFrom(src => src.ConfigName.Trim()))
                .ForMember(dest => dest.IsPublished, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.PublishTime, opt => opt.Ignore());

            CreateMap<EdgeDeviceConfigEntity, EdgeConfigVersionEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.ConfigId, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.SnapshotJson, opt => opt.MapFrom(src => src.ConfigJson))
                .ForMember(dest => dest.PublishedBy, opt => opt.Ignore())
                .ForMember(dest => dest.PublishTime, opt => opt.Ignore());

            CreateMap<EdgeRuleSaveDto, EdgeRuleEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.RuleCode, opt => opt.MapFrom(src => src.RuleCode.Trim()))
                .ForMember(dest => dest.RuleName, opt => opt.MapFrom(src => src.RuleName.Trim()))
                .ForMember(dest => dest.RuleType, opt => opt.MapFrom(src => src.RuleType.Trim()))
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeRulePublishDto, EdgeRuleVersionEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.Version, opt => opt.Ignore())
                .ForMember(dest => dest.SnapshotJson, opt => opt.Ignore())
                .ForMember(dest => dest.PublishTime, opt => opt.Ignore());

            CreateMap<EdgeRuleRollbackDto, EdgeRuleVersionEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DeviceId, opt => opt.Ignore())
                .ForMember(dest => dest.Version, opt => opt.Ignore())
                .ForMember(dest => dest.SnapshotJson, opt => opt.Ignore())
                .ForMember(dest => dest.PublishTime, opt => opt.Ignore());

            CreateMap<EdgeStandardMessageDto, EdgeMessageQueueEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DeviceCode, opt => opt.MapFrom(src => src.DeviceId))
                .ForMember(dest => dest.Status, opt => opt.MapFrom(_ => "Pending"))
                .ForMember(dest => dest.StandardPayloadJson,
                    opt => opt.MapFrom(src => System.Text.Json.JsonSerializer.Serialize(
                        src, System.Text.Json.JsonSerializerOptions.Default)))
                .ForMember(dest => dest.RetryCount, opt => opt.Ignore())
                .ForMember(dest => dest.NextRetryTime, opt => opt.MapFrom(_ => DateTime.Now))
                .ForMember(dest => dest.LastError, opt => opt.Ignore())
                .ForMember(dest => dest.MessageTime, opt => opt.MapFrom(src => src.Timestamp))
                .ForMember(dest => dest.CreateTime, opt => opt.MapFrom(_ => DateTime.Now))
                .ForMember(dest => dest.UploadTime, opt => opt.Ignore());

            CreateMap<EdgeBMappingSaveDto, EdgeBMappingRuleEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.PayloadTemplateName, opt => opt.Ignore())
                .ForMember(dest => dest.SourceId, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.SourceId) ? null : src.SourceId.Trim()))
                .ForMember(dest => dest.SourceDeviceCode, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.SourceDeviceCode) ? null : src.SourceDeviceCode.Trim()))
                .ForMember(dest => dest.MappingName, opt => opt.MapFrom(src => src.MappingName.Trim()))
                .ForMember(dest => dest.Protocol, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.Protocol) ? "HTTP" : src.Protocol.Trim().ToUpperInvariant()))
                .ForMember(dest => dest.TargetUrl, opt => opt.MapFrom(src => src.TargetUrl.Trim()))
                .ForMember(dest => dest.HttpMethod, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.HttpMethod) ? "POST" : src.HttpMethod.Trim().ToUpperInvariant()))
                .ForMember(dest => dest.MqttTopic, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.MqttTopic) ? null : src.MqttTopic.Trim()))
                .ForMember(dest => dest.MqttClientId, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.MqttClientId) ? null : src.MqttClientId.Trim()))
                .ForMember(dest => dest.MqttUsername, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.MqttUsername) ? null : src.MqttUsername.Trim()))
                .ForMember(dest => dest.MqttPassword, opt =>
                {
                    opt.PreCondition(src => src.MqttPassword != null);
                    opt.MapFrom(src => src.MqttPassword);
                })
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeBMappingRuleEntity, EdgeBMappingOutputDto>(MemberList.None)
                .ForMember(dest => dest.Protocol, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.Protocol) ? "HTTP" : src.Protocol))
                .ForMember(dest => dest.HasMqttPassword, opt => opt.MapFrom(src =>
                    !string.IsNullOrEmpty(src.MqttPassword)));

            CreateMap<EdgeBPayloadTemplateSaveDto, EdgeBPayloadTemplateEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.TemplateCode, opt => opt.MapFrom(src => src.TemplateCode.Trim()))
                .ForMember(dest => dest.TemplateName, opt => opt.MapFrom(src => src.TemplateName.Trim()))
                .ForMember(dest => dest.Description, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.Description) ? null : src.Description.Trim()))
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeBPayloadTemplateEntity, EdgeBPayloadTemplateOutputDto>(MemberList.None);

            CreateMap<EdgeBAutoForwardSettingSaveDto, EdgeBAutoForwardSettingEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.LastRunTime, opt => opt.Ignore())
                .ForMember(dest => dest.LastSuccessTime, opt => opt.Ignore())
                .ForMember(dest => dest.LastError, opt => opt.Ignore())
                .ForMember(dest => dest.LastRequestedCount, opt => opt.Ignore())
                .ForMember(dest => dest.LastSuccessCount, opt => opt.Ignore())
                .ForMember(dest => dest.LastFailedCount, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeBAutoForwardSettingEntity, EdgeBAutoForwardSettingOutputDto>(MemberList.None);

            CreateMap<EdgeGatewayFeatureSettingSaveDto, EdgeGatewayFeatureSettingEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedBy, opt => opt.MapFrom(src =>
                    string.IsNullOrWhiteSpace(src.UpdatedBy) ? null : src.UpdatedBy.Trim()))
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeGatewayFeatureSettingEntity, EdgeGatewayFeatureSettingOutputDto>(MemberList.None);

            CreateMap<EdgeRealtimeSnapshotWriteModel, EdgeRealtimeDeviceSnapshotEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DataListJson, opt => opt.MapFrom(src =>
                    JsonConvert.SerializeObject(src.DataList, Formatting.None)))
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore());

            CreateMap<EdgeRealtimeDeviceSnapshotEntity, EdgeRealtimeSnapshotOutputDto>(MemberList.None)
                .ForMember(dest => dest.DeviceMark, opt => opt.MapFrom(src => src.DeviceCode))
                .ForMember(dest => dest.Time, opt => opt.MapFrom(src => src.SourceTimestamp))
                .ForMember(dest => dest.DataList, opt => opt.MapFrom(src =>
                    DeserializeRealtimeDataList(src.DataListJson)))
                .ForMember(dest => dest.Data, opt => opt.MapFrom(src =>
                    DeserializeRealtimeData(src.DataListJson)))
                .ForMember(dest => dest.DataOnline, opt => opt.MapFrom(src =>
                    src.ReceiveTime >= DateTime.Now.Subtract(EdgeRealtimeDataReception.OnlineWindow)))
                .ForMember(dest => dest.DataStatus, opt => opt.MapFrom(src =>
                    src.ReceiveTime >= DateTime.Now.Subtract(EdgeRealtimeDataReception.OnlineWindow)
                        ? EdgeRealtimeDataReception.Receiving
                        : EdgeRealtimeDataReception.Interrupted));

            CreateMap<EdgeRealtimeDeviceSnapshotEntity, EdgeDashboardDataSourceOptionDto>(MemberList.None)
                .ForMember(dest => dest.TenantMark, opt => opt.MapFrom(src => src.TenantMark ?? string.Empty))
                .ForMember(dest => dest.DeviceMark, opt => opt.MapFrom(src => src.DeviceCode))
                .ForMember(dest => dest.DataOnline, opt => opt.MapFrom(src =>
                    src.ReceiveTime >= DateTime.Now.Subtract(EdgeRealtimeDataReception.OnlineWindow)));

            // 机械手看板映射：绑定、状态规则、统计记录和通用部件均由 AutoMapper 统一转换。
            CreateMap<EdgeDashboardRobotBindingSaveDto, EdgeDashboardRobotBindingEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DashboardCode, opt => opt.MapFrom(src => src.DashboardCode.Trim()))
                .ForMember(dest => dest.LineCode, opt => opt.MapFrom(src => src.LineCode.Trim()))
                .ForMember(dest => dest.RobotCode, opt => opt.MapFrom(src => src.RobotCode.Trim()))
                .ForMember(dest => dest.RobotName, opt => opt.MapFrom(src => src.RobotName.Trim()))
                .ForMember(dest => dest.TenantMark, opt => opt.MapFrom(src => src.TenantMark.Trim()))
                .ForMember(dest => dest.DeviceMark, opt => opt.MapFrom(src => src.DeviceMark.Trim()))
                .ForMember(dest => dest.RuleVersion, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardRobotBindingEntity, EdgeDashboardRobotBindingOutputDto>(MemberList.None)
                .ForMember(dest => dest.Fields, opt => opt.Ignore())
                .ForMember(dest => dest.StateRules, opt => opt.Ignore());

            CreateMap<EdgeDashboardRobotFieldSaveDto, EdgeDashboardRobotFieldBindingEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.RobotBindingId, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardRobotFieldBindingEntity, EdgeDashboardRobotFieldSaveDto>(MemberList.None);

            CreateMap<EdgeDashboardDeviceBindingSaveDto, EdgeDashboardDeviceBindingEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DashboardCode, opt => opt.MapFrom(src => src.DashboardCode.Trim()))
                .ForMember(dest => dest.LineCode, opt => opt.MapFrom(src => src.LineCode.Trim()))
                .ForMember(dest => dest.DeviceCode, opt => opt.MapFrom(src => src.DeviceCode.Trim()))
                .ForMember(dest => dest.DeviceName, opt => opt.MapFrom(src => src.DeviceName.Trim()))
                .ForMember(dest => dest.TenantMark, opt => opt.MapFrom(src => src.TenantMark.Trim()))
                .ForMember(dest => dest.SourceDeviceMark, opt => opt.MapFrom(src => src.SourceDeviceMark.Trim()))
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardDeviceBindingEntity, EdgeDashboardDeviceBindingOutputDto>(MemberList.None)
                .ForMember(dest => dest.Fields, opt => opt.Ignore());
            CreateMap<EdgeDashboardDeviceFieldSaveDto, EdgeDashboardDeviceFieldBindingEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DeviceBindingId, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardDeviceFieldBindingEntity, EdgeDashboardDeviceFieldSaveDto>(MemberList.None);

            CreateMap<EdgeDashboardModelDisplayMappingSaveDto, EdgeDashboardModelDisplayMappingEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardModelDisplayMappingEntity, EdgeDashboardModelDisplayMappingOutputDto>(MemberList.None);
            CreateMap<EdgeDashboardDeviceCardSaveDto, EdgeDashboardDeviceCardEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardDeviceCardEntity, EdgeDashboardDeviceCardOutputDto>(MemberList.None)
                .ForMember(dest => dest.Fields, opt => opt.Ignore());
            CreateMap<EdgeDashboardDeviceCardFieldSaveDto, EdgeDashboardDeviceCardFieldEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.CardId, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardDeviceCardFieldEntity, EdgeDashboardDeviceCardFieldSaveDto>(MemberList.None);
            CreateMap<EdgeDashboardDeviceCardChangeRecordEntity, EdgeDashboardDeviceCardChangeRecordOutputDto>(MemberList.None);
            CreateMap<EdgeDashboardDeviceCardAlertRecordEntity, EdgeDashboardDeviceCardAlertRecordOutputDto>(MemberList.None);

            CreateMap<EdgeDashboardRobotStateRuleSaveDto, EdgeDashboardRobotStateRuleEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.RobotBindingId, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardRobotStateRuleEntity, EdgeDashboardRobotStateRuleSaveDto>(MemberList.None);

            CreateMap<EdgeDashboardModelAlarmRuleSaveDto, EdgeDashboardModelAlarmRuleEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DashboardCode, opt => opt.Ignore())
                .ForMember(dest => dest.ModelMark, opt => opt.Ignore())
                .ForMember(dest => dest.RuleVersion, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardModelAlarmRuleEntity, EdgeDashboardModelAlarmRuleOutputDto>(MemberList.None);
            CreateMap<EdgeDashboardModelFieldDisplayMappingSaveDto, EdgeDashboardModelFieldDisplayMappingEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DashboardCode, opt => opt.Ignore())
                .ForMember(dest => dest.ModelMark, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardModelFieldDisplayMappingEntity, EdgeDashboardModelFieldDisplayMappingOutputDto>(MemberList.None);
            CreateMap<EdgeDashboardModelStatePrioritySaveDto, EdgeDashboardModelStatePriorityEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DashboardCode, opt => opt.Ignore())
                .ForMember(dest => dest.ModelMark, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardModelStatePriorityEntity, EdgeDashboardModelStatePriorityOutputDto>(MemberList.None);
            CreateMap<EdgeDashboardModelFieldCatalogEntity, EdgeDashboardSourceFieldOptionDto>(MemberList.None)
                .ForMember(dest => dest.CurrentValue, opt => opt.Ignore());

            CreateMap<EdgeDashboardRobotDailyStatisticEntity, EdgeDashboardDailyStatisticOutputDto>(MemberList.None)
                .ForMember(dest => dest.AvailabilityRate, opt => opt.Ignore());
            CreateMap<EdgeDashboardRobotFaultRecordEntity, EdgeDashboardFaultRecordOutputDto>(MemberList.None);

            CreateMap<EdgeDashboardWidgetSaveDto, EdgeDashboardWidgetEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardWidgetEntity, EdgeDashboardWidgetOutputDto>(MemberList.None)
                .ForMember(dest => dest.Fields, opt => opt.Ignore());
            CreateMap<EdgeDashboardWidgetFieldSaveDto, EdgeDashboardWidgetFieldEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.WidgetId, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardWidgetFieldEntity, EdgeDashboardWidgetFieldSaveDto>(MemberList.None);

            CreateMap<EdgeDashboardLineConfigSaveDto, EdgeDashboardLineConfigEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DashboardCode, opt => opt.MapFrom(src => src.DashboardCode.Trim()))
                .ForMember(dest => dest.LineCode, opt => opt.MapFrom(src => src.LineCode.Trim()))
                .ForMember(dest => dest.LineName, opt => opt.MapFrom(src => src.LineName.Trim()))
                .ForMember(dest => dest.Area, opt => opt.MapFrom(src => src.Area.Trim()))
                .ForMember(dest => dest.Color, opt => opt.MapFrom(src => src.Color.Trim()))
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardLineConfigEntity, EdgeDashboardLineConfigOutputDto>(MemberList.None);

            CreateMap<EdgeDashboardSceneLayoutSaveDto, EdgeDashboardSceneLayoutEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DashboardCode, opt => opt.MapFrom(src => src.DashboardCode.Trim()))
                .ForMember(dest => dest.LineCode, opt => opt.MapFrom(src => src.LineCode.Trim()))
                .ForMember(dest => dest.LineName, opt => opt.MapFrom(src => src.LineName.Trim()))
                .ForMember(dest => dest.Revision, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardSceneLayoutEntity, EdgeDashboardSceneLayoutOutputDto>(MemberList.None);
            CreateMap<EdgeDashboardExportSettingSaveDto, EdgeDashboardExportSettingEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.LastScheduledRunDate, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardExportSettingEntity, EdgeDashboardExportSettingOutputDto>(MemberList.None)
                .ForMember(dest => dest.NextScheduledRunTime, opt => opt.Ignore());
            CreateMap<EdgeDashboardExportRecordEntity, EdgeDashboardExportRecordOutputDto>(MemberList.None);
            CreateMap<EdgeDashboardLineCodeChangeDto, EdgeDashboardSceneLayoutEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DashboardCode, opt => opt.MapFrom(src => src.DashboardCode.Trim()))
                .ForMember(dest => dest.LineCode, opt => opt.MapFrom(src => src.NewLineCode.Trim()))
                .ForMember(dest => dest.LineName, opt => opt.MapFrom(src => src.LineName.Trim()))
                .ForMember(dest => dest.Revision, opt => opt.Ignore())
                .ForMember(dest => dest.IsDelete, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore())
                .ForMember(dest => dest.UpdateTime, opt => opt.Ignore());

            CreateMap<EdgeEventReportDto, EdgeSystemEventEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.MapFrom(_ => DateTime.Now));

            CreateMap<EdgeMessageQueueEntity, EdgeUploadRecordEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.QueueId, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.MappingRuleId, opt => opt.Ignore())
                .ForMember(dest => dest.MappingName, opt => opt.Ignore())
                .ForMember(dest => dest.TargetUrl, opt => opt.Ignore())
                .ForMember(dest => dest.Success, opt => opt.Ignore())
                .ForMember(dest => dest.StatusCode, opt => opt.Ignore())
                .ForMember(dest => dest.ErrorMessage, opt => opt.Ignore())
                .ForMember(dest => dest.ResponseContent, opt => opt.Ignore())
                .ForMember(dest => dest.ResponseContentTruncated, opt => opt.Ignore())
                .ForMember(dest => dest.DurationMs, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore());

            // EdgeGateway Worker 运行映射：负责消息标准化、SQLite 本地队列及生效配置快照转换。
            CreateMap<SourceEnvelope, StandardMessage>(MemberList.None)
                .ForMember(dest => dest.MessageId, opt => opt.Ignore())
                .ForMember(dest => dest.DeviceId, opt => opt.Ignore())
                .ForMember(dest => dest.Timestamp, opt => opt.MapFrom(src => src.ReceivedAt))
                .ForMember(dest => dest.Values, opt => opt.Ignore())
                .ForMember(dest => dest.SchemaVersion, opt => opt.Ignore());

            CreateMap<StandardMessage, EdgeLocalMessageQueueEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.PayloadJson,
                    opt => opt.MapFrom(src => System.Text.Json.JsonSerializer.Serialize(
                        src, System.Text.Json.JsonSerializerOptions.Default)))
                .ForMember(dest => dest.Status, opt => opt.MapFrom(_ => "Pending"))
                .ForMember(dest => dest.RetryCount, opt => opt.Ignore())
                .ForMember(dest => dest.NextRetryTime, opt => opt.MapFrom(_ => DateTime.UtcNow))
                .ForMember(dest => dest.LastError, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.MapFrom(_ => DateTime.UtcNow))
                .ForMember(dest => dest.UploadTime, opt => opt.Ignore());

            CreateMap<EdgeLocalMessageQueueEntity, QueueMessage>(MemberList.None);

            CreateMap<SourceOptions, EdgeLocalSourceConfigEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.SourceId, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.ConfigJson,
                    opt => opt.MapFrom(src => System.Text.Json.JsonSerializer.Serialize(
                        src, System.Text.Json.JsonSerializerOptions.Default)))
                .ForMember(dest => dest.UpdateTime, opt => opt.MapFrom(_ => DateTime.UtcNow));

            CreateMap<EffectiveEdgeConfiguration, EdgeLocalDeviceConfigEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.DeviceId, opt => opt.Ignore())
                .ForMember(dest => dest.Version, opt => opt.MapFrom(src => src.ConfigVersion))
                .ForMember(dest => dest.UpdateTime, opt => opt.MapFrom(_ => DateTime.UtcNow));

            CreateMap<EffectiveEdgeConfiguration, EdgeLocalRuleVersionEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.Version, opt => opt.MapFrom(src => src.RuleVersion))
                .ForMember(dest => dest.SnapshotJson, opt => opt.MapFrom(src => src.RulesJson))
                .ForMember(dest => dest.CreateTime, opt => opt.MapFrom(_ => DateTime.UtcNow));

            CreateMap<QueueMessage, EdgeLocalUploadRecordEntity>(MemberList.None)
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.Success, opt => opt.Ignore())
                .ForMember(dest => dest.StatusCode, opt => opt.Ignore())
                .ForMember(dest => dest.ErrorMessage, opt => opt.Ignore())
                .ForMember(dest => dest.CreateTime, opt => opt.Ignore());
        }

        /// <summary>把数据库中的动态数据 JSON 还原为兼容列表。</summary>
        private static List<EdgeRealtimeDataItemDto> DeserializeRealtimeDataList(string json) =>
            JsonConvert.DeserializeObject<List<EdgeRealtimeDataItemDto>>(json) ?? [];

        /// <summary>从兼容列表提取新上报信封使用的动态 data 对象。</summary>
        private static Dictionary<string, object?> DeserializeRealtimeData(string json) =>
            DeserializeRealtimeDataList(json).FirstOrDefault()?.Data ?? [];
    }
}

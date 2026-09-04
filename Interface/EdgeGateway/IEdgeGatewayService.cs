using Model.EdgeGateway;

namespace Interface;

/// <summary>
/// EdgeGateway 中心端业务接口，负责设备、配置、规则、队列和 B 系统转发管理。
/// </summary>
public interface IEdgeGatewayService
{
    /// <summary>获取边缘网关运行概览。</summary>
    Task<EdgeOverviewDto> GetOverviewAsync();
    /// <summary>生成配置工作簿并以可顺序读取的文件流返回，供 HTTP 大文件下载使用。</summary>
    Task<EdgeGatewayTemplateStreamDto> ExportConfigurationTemplateStreamAsync(
        bool includeData,
        CancellationToken cancellationToken = default);
    Task<EdgeGatewayTemplateFileDto> ExportConfigurationTemplateAsync(bool includeData);
    Task<EdgeGatewayImportResultDto> ImportConfigurationAsync(string fileName, Stream stream, string? operatorName);
    /// <summary>分页查询中心端登记的边缘设备。</summary>
    Task<EdgePagedResultDto<EdgeDeviceOutputDto>> GetDevicePageAsync(EdgeDeviceQueryDto query);
    /// <summary>新增或修改边缘设备基础信息。</summary>
    Task<int> SaveDeviceAsync(EdgeDeviceSaveDto dto);
    /// <summary>启用或停用指定边缘设备。</summary>
    Task<bool> SetDeviceEnabledAsync(int id, bool enabled);
    /// <summary>软删除指定边缘设备，并阻止同编码设备通过心跳自动恢复。</summary>
    Task<bool> DeleteDeviceAsync(int id);
    /// <summary>接收边缘节点心跳并返回中心端最新版本号。</summary>
    Task<EdgeHeartbeatResultDto> ReceiveHeartbeatAsync(EdgeHeartbeatDto dto);

    /// <summary>查询指定设备的采集配置历史。</summary>
    Task<List<EdgeDeviceConfigEntity>> GetDeviceConfigsAsync(int deviceId);
    /// <summary>保存指定设备的采集配置草稿。</summary>
    Task<int> SaveDeviceConfigAsync(int deviceId, EdgeDeviceConfigSaveDto dto);
    /// <summary>发布采集配置，使其可以被边缘节点获取。</summary>
    Task<bool> PublishDeviceConfigAsync(int configId, string? publishedBy);
    /// <summary>软删除非当前生效的采集配置，发布快照继续保留。</summary>
    Task<bool> DeleteDeviceConfigAsync(int configId);
    /// <summary>按设备编码获取连接、采集配置和规则的生效快照。</summary>
    Task<EdgeEffectiveConfigDto?> GetEffectiveConfigAsync(string deviceCode);

    /// <summary>分页查询边缘规则。</summary>
    Task<EdgePagedResultDto<EdgeRuleEntity>> GetRulePageAsync(EdgeRuleQueryDto query);
    /// <summary>新增或修改边缘规则。</summary>
    Task<int> SaveRuleAsync(EdgeRuleSaveDto dto);
    /// <summary>发布设备规则版本。</summary>
    Task<int> PublishRulesAsync(EdgeRulePublishDto dto);
    /// <summary>以历史快照生成一个新的规则版本。</summary>
    Task<int> RollbackRulesAsync(EdgeRuleRollbackDto dto);
    /// <summary>查询设备或全局规则版本历史。</summary>
    Task<List<EdgeRuleVersionEntity>> GetRuleVersionsAsync(int? deviceId);

    /// <summary>分页查询中心端消息队列。</summary>
    Task<EdgePagedResultDto<EdgeMessageQueueEntity>> GetQueuePageAsync(EdgeQueueQueryDto query);
    /// <summary>分页查询中心端到 B 系统的每次上传结果。</summary>
    Task<EdgePagedResultDto<EdgeUploadRecordEntity>> GetUploadRecordPageAsync(EdgeUploadRecordQueryDto query);
    /// <summary>分页查询边缘节点向上游 WebSocket 发送报文的结果。</summary>
    Task<EdgePagedResultDto<EdgeWebSocketSendRecordEntity>> GetWebSocketSendRecordPageAsync(
        EdgeWebSocketSendRecordQueryDto query);
    /// <summary>接收边缘节点上报的 WebSocket 报文发送结果。</summary>
    Task<long> ReportWebSocketSendRecordAsync(EdgeWebSocketSendRecordReportDto dto);
    /// <summary>分页查询最近七天 WebSocket 接收数据日表。</summary>
    Task<EdgePagedResultDto<EdgeWebSocketReceiveRecordEntity>> GetWebSocketReceiveRecordPageAsync(
        EdgeWebSocketReceiveRecordQueryDto query);
    /// <summary>接收边缘节点上传的标准消息并写入中心队列。</summary>
    Task<EdgeMessageAcceptResultDto> ReceiveMessagesAsync(EdgeMessageBatchDto dto);
    /// <summary>将失败队列重置为待处理状态。</summary>
    Task<int> ResetQueueAsync(EdgeRetryRequestDto dto);
    Task<int> DeleteQueueAsync(EdgeBatchIdsDto dto);
    /// <summary>把指定中心队列消息转换并转发到 B 系统。</summary>
    Task<EdgeRetryResultDto> ForwardQueueAsync(EdgeRetryRequestDto dto);
    /// <summary>按重试时间读取到期消息，供中心端自动转发服务调用。</summary>
    Task<EdgeRetryResultDto> ForwardDueQueueAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>分页查询边缘网关事件。</summary>
    Task<EdgePagedResultDto<EdgeSystemEventEntity>> GetEventPageAsync(EdgeEventQueryDto query);
    /// <summary>保存边缘节点上报的运行事件。</summary>
    Task<long> ReportEventAsync(EdgeEventReportDto dto);
    /// <summary>查询 B 系统字段映射及目标地址配置。</summary>
    Task<List<EdgeBMappingOutputDto>> GetBMappingRulesAsync();
    /// <summary>按关键字、设备、协议和启停状态分页查询 B 系统映射。</summary>
    Task<EdgePagedResultDto<EdgeBMappingOutputDto>> GetBMappingRulePageAsync(
        EdgeBMappingQueryDto query);
    /// <summary>新增或修改 B 系统映射配置。</summary>
    Task<int> SaveBMappingRuleAsync(EdgeBMappingSaveDto dto);
    Task<int> DeleteBMappingRulesAsync(EdgeBatchIdsDto dto);
}

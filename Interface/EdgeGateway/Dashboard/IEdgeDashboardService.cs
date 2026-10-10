using Model.EdgeGateway;

namespace Interface;

/// <summary>机械手绑定、统计查询和数据库驱动看板部件的业务接口。</summary>
public interface IEdgeDashboardService
{
    /// <summary>按 modelMark 汇总可绑定 HTTP 数据源的模型大类。</summary>
    Task<List<EdgeDashboardDataSourceModelOptionDto>> GetDataSourceModelsAsync();

    /// <summary>在指定模型大类内按设备标识检索可绑定的 HTTP 数据源。</summary>
    Task<List<EdgeDashboardDataSourceOptionDto>> GetDataSourcesByModelAsync(
        string modelMark,
        string? keyword);

    /// <summary>查询可以绑定到机械手的 HTTP 最新数据源。</summary>
    Task<List<EdgeDashboardDataSourceOptionDto>> GetDataSourcesAsync(string? keyword);

    /// <summary>读取一台数据源完整报文中的可选叶子字段。</summary>
    Task<List<EdgeDashboardSourceFieldOptionDto>> GetSourceFieldsAsync(
        string tenantMark,
        string deviceMark);

    /// <summary>读取一个模型最近样本中的可选叶子字段，供模型级报警规则选择。</summary>
    Task<List<EdgeDashboardSourceFieldOptionDto>> GetModelSourceFieldsAsync(string modelMark);

    /// <summary>查询指定模型已经保存的报警映射规则。</summary>
    Task<List<EdgeDashboardModelAlarmRuleOutputDto>> GetModelAlarmRulesAsync(
        string dashboardCode,
        string modelMark);

    /// <summary>整体保存指定模型的报警映射规则。</summary>
    Task<List<EdgeDashboardModelAlarmRuleOutputDto>> SaveModelAlarmRulesAsync(
        EdgeDashboardModelAlarmRuleListSaveDto dto);

    /// <summary>查询或整体保存某一模型的统一字段展示映射。</summary>
    Task<List<EdgeDashboardModelFieldDisplayMappingOutputDto>> GetModelFieldDisplayMappingsAsync(
        string dashboardCode,
        string modelMark);
    Task<List<EdgeDashboardModelFieldDisplayMappingOutputDto>> SaveModelFieldDisplayMappingsAsync(
        EdgeDashboardModelFieldDisplayMappingListSaveDto dto);

    /// <summary>查询或整体保存按模型共享的状态冲突优先级。</summary>
    Task<List<EdgeDashboardModelStatePriorityOutputDto>> GetModelStatePrioritiesAsync(
        string dashboardCode,
        string modelMark);
    Task<List<EdgeDashboardModelStatePriorityOutputDto>> SaveModelStatePrioritiesAsync(
        EdgeDashboardModelStatePriorityListSaveDto dto);

    /// <summary>查询一条线体已经保存的机械手绑定。</summary>
    Task<List<EdgeDashboardRobotBindingOutputDto>> GetBindingsAsync(string lineCode);

    /// <summary>保存机械手数据源、展示字段和运行状态规则。</summary>
    Task<EdgeDashboardRobotBindingOutputDto> SaveBindingAsync(EdgeDashboardRobotBindingSaveDto dto);

    /// <summary>查询一条线体的 Three.js 设备数据绑定。</summary>
    Task<List<EdgeDashboardDeviceBindingOutputDto>> GetDeviceBindingsAsync(string lineCode);

    /// <summary>保存 Three.js 设备的数据源和展示字段绑定。</summary>
    Task<EdgeDashboardDeviceBindingOutputDto> SaveDeviceBindingAsync(EdgeDashboardDeviceBindingSaveDto dto);

    /// <summary>维护数据模型标识到业务设备名称的映射。</summary>
    Task<List<EdgeDashboardModelDisplayMappingOutputDto>> GetModelDisplayMappingsAsync(
        string dashboardCode);
    Task<EdgeDashboardModelDisplayMappingOutputDto> SaveModelDisplayMappingAsync(
        EdgeDashboardModelDisplayMappingSaveDto dto);
    Task DeleteModelDisplayMappingAsync(long id);

    /// <summary>查询设备卡片配置；lineCode 为空时返回所有线体。</summary>
    Task<List<EdgeDashboardDeviceCardOutputDto>> GetDeviceCardsAsync(string? lineCode);
    Task<EdgeDashboardDeviceCardOutputDto> SaveDeviceCardAsync(EdgeDashboardDeviceCardSaveDto dto);
    Task DeleteDeviceCardAsync(long id);
    Task<EdgeDashboardDeviceCardBoardSettingDto> SaveDeviceCardBoardSettingAsync(
        EdgeDashboardDeviceCardBoardSettingDto dto);
    Task<EdgeDashboardDeviceCardBoardRuntimeDto> GetDeviceCardBoardRuntimeAsync(string? lineCode);
    Task<EdgePagedResultDto<EdgeDashboardDeviceCardChangeRecordOutputDto>> GetDeviceCardChangeRecordsAsync(
        EdgeDashboardDeviceCardChangeQueryDto query);
    Task<EdgePagedResultDto<EdgeDashboardDeviceCardAlertRecordOutputDto>> GetDeviceCardAlertRecordsAsync(
        EdgeDashboardDeviceCardAlertQueryDto query);
    Task<List<EdgeDashboardDeviceCardAlertRecordOutputDto>> ClaimDeviceCardAlertsAsync(
        string consumerCode,
        int maximum,
        string? lineCode,
        string? deviceMark,
        string? fieldCode,
        CancellationToken cancellationToken = default);
    Task<int> AcknowledgeDeviceCardAlertsAsync(
        string consumerCode,
        IReadOnlyCollection<long> alertIds,
        bool sent,
        string? message,
        CancellationToken cancellationToken = default);

    /// <summary>实时快照接收成功后按配置追踪字段变更；首次值只建立检查点。</summary>
    Task TrackDeviceCardChangesAsync(EdgeDashboardTelemetrySampleDto sample, CancellationToken cancellationToken);

    /// <summary>读取或保存一条线体的产量来源设备和字段。</summary>
    Task<EdgeDashboardLineOutputBindingOutputDto?> GetLineOutputBindingAsync(string lineCode);
    Task<EdgeDashboardLineOutputBindingOutputDto> SaveLineOutputBindingAsync(EdgeDashboardLineOutputBindingSaveDto dto);

    /// <summary>读取当前线体根据已绑定字段累计出的当日产出。</summary>
    Task<EdgeDashboardLineOutputDto> GetLineOutputAsync(string lineCode);

    /// <summary>软删除 Three.js 设备绑定。</summary>
    Task DeleteDeviceBindingAsync(long id);

    /// <summary>软删除机械手绑定，并结束尚未关闭的运行状态和故障。</summary>
    Task DeleteBindingAsync(long id);

    /// <summary>批量返回一条线体所有机械手的当前状态和配置字段。</summary>
    Task<EdgeDashboardLineRuntimeDto> GetLineRuntimeAsync(string lineCode);

    /// <summary>分页查询按日累计的运行、空闲和故障统计。</summary>
    Task<EdgePagedResultDto<EdgeDashboardDailyStatisticOutputDto>> GetDailyStatisticsAsync(
        EdgeDashboardDailyStatisticQueryDto query);

    /// <summary>分页查询故障生命周期记录。</summary>
    Task<EdgePagedResultDto<EdgeDashboardFaultRecordOutputDto>> GetFaultRecordsAsync(
        EdgeDashboardFaultQueryDto query);

    /// <summary>按原因汇总指定时间范围内的故障次数和持续时间。</summary>
    Task<List<EdgeDashboardFaultSummaryDto>> GetFaultSummaryAsync(
        EdgeDashboardFaultQueryDto query,
        int topN);

    /// <summary>查询数据库驱动的表格、饼图和柱状图配置。</summary>
    Task<List<EdgeDashboardWidgetOutputDto>> GetWidgetsAsync(string dashboardCode, string? lineCode);

    /// <summary>新增或修改一个通用看板部件。</summary>
    Task<EdgeDashboardWidgetOutputDto> SaveWidgetAsync(EdgeDashboardWidgetSaveDto dto);

    /// <summary>软删除看板部件。</summary>
    Task DeleteWidgetAsync(long id);

    /// <summary>返回不同数据源允许绑定到部件的字段目录。</summary>
    List<EdgeDashboardFieldCatalogDto> GetWidgetFieldCatalog();

    /// <summary>
    /// 一次返回看板首屏所需的线体列表和全部 Three.js 场景。
    /// 仅管理端允许在读取过程中迁移旧版场景数据；公开只读页必须关闭该行为。
    /// </summary>
    Task<EdgeDashboardBootstrapOutputDto> GetBootstrapAsync(
        string dashboardCode,
        bool migrateLegacyScenes = true);

    /// <summary>新增或修改一条看板线体主配置。</summary>
    Task<EdgeDashboardLineConfigOutputDto> SaveLineConfigAsync(
        EdgeDashboardLineConfigSaveDto dto);

    /// <summary>复制线体的可编辑看板配置到新线体，不复制运行、统计、告警和追溯历史。</summary>
    Task<EdgeDashboardLineConfigOutputDto> CopyLineConfigAsync(EdgeDashboardLineCopyDto dto);

    /// <summary>软删除线体主配置及对应的 Three.js 场景。</summary>
    Task DeleteLineConfigAsync(string dashboardCode, string lineCode);

    /// <summary>读取指定看板和线体的 Three.js 场景组态；未配置时返回空。</summary>
    Task<EdgeDashboardSceneLayoutOutputDto?> GetSceneLayoutAsync(
        string dashboardCode,
        string lineCode);

    /// <summary>新增或覆盖指定线体的完整 Three.js 场景组态。</summary>
    Task<EdgeDashboardSceneLayoutOutputDto> SaveSceneLayoutAsync(
        EdgeDashboardSceneLayoutSaveDto dto);

    /// <summary>修改线体编码，并在事务中同步迁移看板的全部关联数据。</summary>
    Task<EdgeDashboardSceneLayoutOutputDto> ChangeLineCodeAsync(
        EdgeDashboardLineCodeChangeDto dto);
}

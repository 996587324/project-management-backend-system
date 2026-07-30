using Model.EdgeGateway;

namespace Interface;

/// <summary>
/// 将数据源原始报文转换为标准消息并执行边缘规则。
/// </summary>
public interface IEdgeMessageProcessor
{
    /// <summary>解析、映射并校验单条数据源报文。</summary>
    ProcessResult Process(SourceEnvelope envelope);

    /// <summary>用中心端已发布快照替换当前内存中的字段映射和边缘规则。</summary>
    void ApplyConfiguration(string configJson, string rulesJson);
}

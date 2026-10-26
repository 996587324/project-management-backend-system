using Interface;
using Model.HourlyProduction;
using SqlSugar;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace Service;

/// <summary>接收实际增量帧；在快照旧时间过滤之前持久化，接收失败必须让上游重试，不能静默丢数。</summary>
public sealed class HourlyProductionIngress(SqlSugarScope scope, HourlyProductionCatalog catalog) : IHourlyProductionIngress
{
    public async Task CaptureRealtimeAsync(string source, string device, string? messageId, DateTime occurredAt,
        IReadOnlyList<string> frames, CancellationToken cancellationToken)
    {
        using var db = scope.GetConnection("calmadmin").CopyNew();
        // 全新安装且尚无小时配置时，不让本模块初始化阻止原有实时接收。
        if (!catalog.Ready && !db.DbMaintenance.IsAnyTable("HourlyProductionBoard", false)) return;
        // 以数据库已提交配置为准，避免其他实例保存绑定后，本机缓存窗口内丢失生产事件。
        var candidates = (await db.Queryable<HourlyProductionBoardEntity>().ToListAsync())
            .Select(row => JsonSerializer.Deserialize<HourlyBoardDefinition>(row.DefinitionJson)!)
            .SelectMany(board => board.Lines).SelectMany(line => line.Processes).Where(process => process.Enabled)
            .SelectMany(process => process.Bindings.Where(binding => binding.Kind == "Realtime" &&
                binding.SourceKey == source && binding.DeviceKey == device).Select(binding => (Process: process, Binding: binding))).ToArray();
        if (candidates.Length == 0) return;
        for (var index = 0; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(frames[index]))
            {
                DateParseHandling = Newtonsoft.Json.DateParseHandling.None
            };
            var values = JObject.Load(reader);
            // 温度等无关增量不进入过站暂存，避免大量遥测挤占有效生产事件处理能力。
            if (!candidates.Any(candidate =>
                    (candidate.Process.Mode != "DistinctSn" || HourlyProductionService.Token(values, candidate.Binding.SnPath) != null) &&
                    (candidate.Process.Mode != "Sum" || HourlyProductionService.Token(values, candidate.Binding.QuantityPath) != null) &&
                    (candidate.Binding.FilterPath.Length == 0 || HourlyProductionService.Text(HourlyProductionService.Token(values,
                        candidate.Binding.FilterPath)) == candidate.Binding.FilterValue))) continue;
            if (!catalog.Ready) throw new InvalidOperationException("已配置的小时统计接收暂存正在准备，请稍后重试上报");
            if (string.IsNullOrWhiteSpace(messageId))
                throw new ArgumentException("参与小时产量统计的上报必须提供稳定messageId，以便重复上传去重");
            if (occurredAt.Year < 2000 || occurredAt.Year > 2100)
                throw new ArgumentException("参与小时统计的报文时间必须在2000至2100年范围内");
            var row = new HourlyProductionInboxEntity
            {
                EventKey = HourlyProductionRules.Hash($"rt\u001f{source}\u001f{device}\u001f{messageId}\u001f{index}"),
                SourceKey = source, DeviceKey = device, ValuesJson = frames[index],
                OccurredAt = occurredAt, ReceivedAt = HourlyProductionRules.Now
            };
            if (await db.Queryable<HourlyProductionInboxEntity>().AnyAsync(x => x.EventKey == row.EventKey)) continue;
            try
            {
                await db.Insertable(row).ExecuteCommandAsync();
            }
            catch
            {
                if (!await db.Queryable<HourlyProductionInboxEntity>().AnyAsync(x => x.EventKey == row.EventKey)) throw;
            }
        }
    }
}

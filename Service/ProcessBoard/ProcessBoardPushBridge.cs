using System.Security.Cryptography;
using System.Text;
using Model.EdgeGateway;
using Model.ProcessBoard;
using Model.User;
using Model.WeChatClawBot;
using SqlSugar;

namespace Service;

/// <summary>共用发送队列和全局节流；负 CardId/RuleId 显式区分工艺卡片及配置版本。</summary>
public static class ProcessBoardPushBridge
{
    public static async Task EnqueueAsync(ISqlSugarClient db, ProcessBoardDefinition card,
        ProcessBoardDefinition line, ProcessFieldDefinition field, ProcessFieldRuntime runtime, ProcessBoardEventEntity record)
    {
        var ids = field.RecipientUserIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var accounts = await db.Queryable<WeChatClawBotAccountEntity>().Where(x => ids.Contains(x.UserId)).ToListAsync();
        var users = await db.Queryable<SysUserInfo>().Where(x => ids.Contains(x.UserId)).ToListAsync();
        var label = record.EventType switch
        {
            "UpperExceeded" => "超过上限", "LowerExceeded" => "低于下限", "Recovered" => "恢复正常", _ => "参数变化"
        };
        var content = $"【工艺参数通知】{label}\n线体：{line.Name}\n卡片：{card.Name}\n参数：{field.Name}\n" +
            $"原值：{record.PreviousJson}\n当前值：{runtime.ValueJson}{field.Unit}\n" +
            $"范围：{field.LowerLimit} ～ {field.UpperLimit}\n数据时间：{runtime.DataTime:yyyy-MM-dd HH:mm:ss}";
        var eventKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"process-event:{record.Id}")));
        var tasks = ids.Select(id => new EdgeDeviceCardPushTaskEntity
        {
            CardId = -card.Id, RuleId = -card.Version, LineCode = $"process:{line.Id}",
            FieldCode = field.Code, DisplayName = field.Name, DeviceMark = field.Source.DeviceMark,
            UserId = id, UserName = users.FirstOrDefault(x => x.UserId == id)?.UserName,
            BindingIdentity = accounts.FirstOrDefault(x => x.UserId == id) is { } account
                ? WeChatClawBotBindingIdentity.Create(account) : null,
            EventKey = eventKey, EventType = record.EventType, Content = content,
            DataTime = runtime.DataTime ?? runtime.CalculatedAt, CreateTime = runtime.CalculatedAt,
            UpdateTime = runtime.CalculatedAt, NextAttemptTime = runtime.CalculatedAt
        }).ToArray();
        if (tasks.Length > 0) await db.Insertable(tasks).ExecuteCommandAsync();
    }

    public static async Task<bool> IsCurrentAsync(ISqlSugarClient db, EdgeDeviceCardPushTaskEntity task)
    {
        var row = await db.Queryable<ProcessBoardConfigEntity>().InSingleAsync(-task.CardId);
        if (row == null || !row.Enabled || row.Kind is not ("Card" or "Line") || row.Version != -task.RuleId) return false;
        var line = row.Kind == "Line" ? row : await db.Queryable<ProcessBoardConfigEntity>().InSingleAsync(row.ParentId);
        if (line is not { Kind: "Line", Enabled: true }) return false;
        var region = await db.Queryable<ProcessBoardConfigEntity>().InSingleAsync(line.ParentId);
        if (region is not { Kind: "Region", Enabled: true }) return false;
        var field = ProcessBoardService.Read(row).Fields.FirstOrDefault(x => x.Code == task.FieldCode);
        return field?.PushEnabled == true && field.RecipientUserIds.Contains(task.UserId, StringComparer.OrdinalIgnoreCase) &&
            (task.EventType == "Changed" ? field.PushOnChange : task.EventType == "Recovered" ? field.PushOnRecovery : field.PushOnLimit);
    }
}

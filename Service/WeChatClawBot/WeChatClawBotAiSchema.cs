using Model.WeChatClawBot;
using SqlSugar;

namespace Service;

public static class WeChatClawBotAiSchema
{
    public static void Initialize(ISqlSugarClient db)
    {
        // 扩展列显式允许 SQL NULL，由 CodeFirst 兼容非空旧表；NULL 允许列表沿用旧版 AgentCode。
        db.CodeFirst.InitTables<WeChatClawBotAiConnectionEntity, WeChatClawBotAiDayEntity,
            WeChatClawBotAiJobEntity, WeChatClawBotSendBudgetEntity>();
    }
}

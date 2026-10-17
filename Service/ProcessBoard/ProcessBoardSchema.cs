using Model.ProcessBoard;
using SqlSugar;

namespace Service;

public static class ProcessBoardSchema
{
    public static async Task InitializeAsync(ISqlSugarClient db)
    {
        db.CodeFirst.InitTables(typeof(ProcessBoardConfigEntity), typeof(ProcessBoardValueEntity),
            typeof(ProcessBoardSampleEntity), typeof(ProcessBoardEventEntity),
            typeof(ProcessBoardConfigAuditEntity), typeof(ProcessBoardSettingEntity), typeof(ProcessBoardExportEntity));
        if (!await db.Queryable<ProcessBoardSettingEntity>().AnyAsync(x => x.Id == 1))
            await db.Insertable(new ProcessBoardSettingEntity { UpdateTime = DateTime.Now }).ExecuteCommandAsync();
    }
}

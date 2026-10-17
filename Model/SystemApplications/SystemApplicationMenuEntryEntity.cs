using SqlSugar;

namespace Model.SystemApplications;

/// <summary>系统内独立菜单实例。复用组件，但不复用另一系统的菜单名称、路径和排序。</summary>
[SugarTable("SysApplicationMenuEntry")]
[SugarIndex("IX_SysApplicationMenuEntry_AppParent", nameof(AppCode), OrderByType.Asc, nameof(ParentId), OrderByType.Asc, false)]
public sealed class SysApplicationMenuEntryEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(Length = 32)]
    public string AppCode { get; set; } = "B";
    public int ParentId { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? SourceMenuId { get; set; }
    [SugarColumn(Length = 128)]
    public string PermissionKey { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string DefinitionJson { get; set; } = "{}";
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

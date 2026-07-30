using System.Text.Json;
using AutoMapper;
using Interface;
using Model.EdgeGateway;
using SqlSugar;

namespace Service;

/// <summary>
/// B 系统 payload 模板服务，使用 SqlSugar 保存任意对象或数组 JSON 模板。
/// </summary>
public class EdgeBPayloadTemplateService : IEdgeBPayloadTemplateService
{
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    /// <summary>注入项目 SqlSugar 连接和 AutoMapper，表结构由启动初始化服务统一维护。</summary>
    public EdgeBPayloadTemplateService(SqlSugarScope dbScope, IMapper mapper)
    {
        _dbScope = dbScope;
        _mapper = mapper;
    }

    /// <summary>查询模板列表并通过 AutoMapper 转换管理端输出模型。</summary>
    public async Task<List<EdgeBPayloadTemplateOutputDto>> GetListAsync()
    {
        var entities = await Db.Queryable<EdgeBPayloadTemplateEntity>()
            .Where(x => x.IsDelete == 0)
            .OrderByDescending(x => x.UpdateTime)
            .OrderByDescending(x => x.CreateTime)
            .ToListAsync();
        return _mapper.Map<List<EdgeBPayloadTemplateOutputDto>>(entities);
    }

    /// <summary>校验模板 JSON 和模板编码后新增或更新模板。</summary>
    public async Task<int> SaveAsync(EdgeBPayloadTemplateSaveDto dto)
    {
        NormalizeAndValidate(dto);
        var duplicateCode = await Db.Queryable<EdgeBPayloadTemplateEntity>()
            .AnyAsync(x => x.IsDelete == 0 && x.TemplateCode == dto.TemplateCode && x.Id != dto.Id);
        if (duplicateCode) throw new InvalidOperationException($"模板编码已存在：{dto.TemplateCode}");

        if (dto.Id > 0)
        {
            var entity = await Db.Queryable<EdgeBPayloadTemplateEntity>()
                .FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0);
            if (entity == null) throw new InvalidOperationException("B 系统模板不存在");
            _mapper.Map(dto, entity);
            entity.UpdateTime = DateTime.Now;
            await Db.Updateable(entity).ExecuteCommandAsync();
            await AddOperationAsync("BPayloadTemplate.Update", entity.Id.ToString(), dto);
            return entity.Id;
        }

        var created = _mapper.Map<EdgeBPayloadTemplateEntity>(dto);
        created.CreateTime = DateTime.Now;
        created.UpdateTime = DateTime.Now;
        var id = await Db.Insertable(created).ExecuteReturnIdentityAsync();
        await AddOperationAsync("BPayloadTemplate.Create", id.ToString(), dto);
        return id;
    }

    /// <summary>软删除模板，映射表中已经保存的 JSON 快照不随模板删除。</summary>
    public async Task<bool> DeleteAsync(int id)
    {
        var entity = await Db.Queryable<EdgeBPayloadTemplateEntity>()
            .FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (entity == null) throw new InvalidOperationException("B 系统模板不存在");
        entity.IsDelete = 1;
        entity.UpdateTime = DateTime.Now;
        await Db.Updateable(entity).ExecuteCommandAsync();
        await AddOperationAsync(
            "BPayloadTemplate.Delete",
            id.ToString(),
            new { entity.TemplateCode, entity.TemplateName });
        return true;
    }

    /// <summary>规范化模板字段，并确认模板根节点为 JSON 对象或数组。</summary>
    private static void NormalizeAndValidate(EdgeBPayloadTemplateSaveDto dto)
    {
        dto.TemplateCode = dto.TemplateCode?.Trim() ?? string.Empty;
        dto.TemplateName = dto.TemplateName?.Trim() ?? string.Empty;
        dto.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        if (string.IsNullOrWhiteSpace(dto.TemplateCode))
            throw new InvalidOperationException("模板编码不能为空");
        if (string.IsNullOrWhiteSpace(dto.TemplateName))
            throw new InvalidOperationException("模板名称不能为空");
        if (dto.TemplateCode.Length > 64)
            throw new InvalidOperationException("模板编码不能超过 64 个字符");
        if (dto.TemplateCode.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.' or '-')))
            throw new InvalidOperationException("模板编码只能包含字母、数字、下划线、点和短横线");
        if (dto.TemplateName.Length > 128)
            throw new InvalidOperationException("模板名称不能超过 128 个字符");
        if (dto.Description?.Length > 256)
            throw new InvalidOperationException("模板说明不能超过 256 个字符");
        if (string.IsNullOrWhiteSpace(dto.TemplateJson))
            throw new InvalidOperationException("模板 JSON 不能为空");

        try
        {
            using var document = JsonDocument.Parse(dto.TemplateJson);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new InvalidOperationException("模板 JSON 根节点必须是对象或数组");
            dto.TemplateJson = JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"模板 JSON 格式错误：{ex.Message}");
        }
    }

    /// <summary>记录模板管理操作，模板内容只保存在中心端操作日志中。</summary>
    private Task AddOperationAsync(string operationType, string target, object detail) =>
        Db.Insertable(new EdgeOperationLogEntity
        {
            OperationType = operationType,
            Target = target,
            DetailJson = JsonSerializer.Serialize(detail),
            CreateTime = DateTime.Now
        }).ExecuteCommandAsync();
}

using System.Text.Json;
using Model.ExternalTools;

namespace Interface;

/// <summary>外部 MCP/OpenAPI 工具统一入口；所有公开能力必须在此显式登记。</summary>
public interface IExternalToolService
{
    Task<ExternalToolClientSecretResultDto> SaveClientAsync(ExternalToolClientSaveDto dto);
    Task<List<ExternalToolClientOutputDto>> GetClientsAsync();
    Task<ExternalToolServerSettingDto> GetServerSettingAsync();
    Task<ExternalToolServerSettingDto> SaveServerSettingAsync(ExternalToolServerSettingSaveDto dto, string updatedBy);
    Task<ExternalToolTokenResponseDto?> IssueClientTokenAsync(ExternalToolTokenRequestDto dto);
    IReadOnlyList<ExternalToolDefinitionDto> GetDefinitions(ExternalToolCallerContext caller);
    Task<ExternalToolExecutionResultDto> ExecuteAsync(
        string toolName,
        JsonElement arguments,
        ExternalToolCallerContext caller,
        string requestId,
        CancellationToken cancellationToken = default);
}

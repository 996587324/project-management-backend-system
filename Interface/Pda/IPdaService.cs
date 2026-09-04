using Model.Pda;

namespace Interface;

public interface IPdaService
{
    Task<PdaLoginResult> LoginAsync(PdaLoginRequest request);
    Task<PdaScanResultDto> QueryProductBarcodeAsync(string userId, string userName, string deviceCode, PdaScanQueryRequest request, CancellationToken cancellationToken);
    Task<PdaPageResult<PdaScanLogDto>> GetScanLogsAsync(PdaScanLogQueryDto query);
    Task<PdaScanLogDto?> GetScanLogAsync(long id);
    Task<PdaPageResult<PdaScanLogDto>> GetRecentScanLogsAsync(string userId, string deviceCode, int current, int size);
    Task<PdaPageResult<PdaDeviceDto>> GetDevicesAsync(PdaDeviceQueryDto query);
    Task<PdaDeviceDto> SaveDeviceAsync(PdaDeviceSaveDto dto);
    Task<bool> DeleteDeviceAsync(long id);
    Task<PdaPageResult<PdaBarcodeRuleDto>> GetBarcodeRulesAsync(PdaBarcodeRuleQueryDto query);
    Task<PdaBarcodeRuleDto> SaveBarcodeRuleAsync(PdaBarcodeRuleSaveDto dto);
    Task<bool> DeleteBarcodeRuleAsync(long id);
}

using SqlSugar;

namespace Model.Pda;

public class PdaOptions
{
    public string ProductBarcodeQueryUrl { get; set; } = string.Empty;
    public string ProductBarcodeApiKey { get; set; } = string.Empty;
    public int ProductBarcodeTimeoutSeconds { get; set; } = 8;
    public string RemoteBarcodeRuleValidationUrl { get; set; } = string.Empty;
    public string RemoteBarcodeRuleValidationApiKey { get; set; } = string.Empty;
    public int RemoteBarcodeRuleValidationTimeoutSeconds { get; set; } = 8;
}

public class PdaLoginRequest
{
    public string userName { get; set; } = string.Empty;
    public string password { get; set; } = string.Empty;
    public string deviceCode { get; set; } = string.Empty;
}

public class PdaLoginResult
{
    public string token { get; set; } = string.Empty;
    public string refreshToken { get; set; } = string.Empty;
    public string userId { get; set; } = string.Empty;
    public string userName { get; set; } = string.Empty;
    public List<string> roles { get; set; } = new();
    public List<string> permissionKeys { get; set; } = new();
    public PdaDeviceDto device { get; set; } = new();
}

public class PdaDeviceQueryDto
{
    public int current { get; set; } = 1;
    public int size { get; set; } = 20;
    public string? keyword { get; set; }
    public bool? enabled { get; set; }
}

public class PdaDeviceSaveDto
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long? id { get; set; }
    public string deviceCode { get; set; } = string.Empty;
    public string deviceName { get; set; } = string.Empty;
    public string? model { get; set; }
    public string scannerMode { get; set; } = "Keyboard";
    public bool enabled { get; set; } = true;
    public string? boundUserId { get; set; }
}

public class PdaDeviceDto
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long id { get; set; }
    public string deviceCode { get; set; } = string.Empty;
    public string deviceName { get; set; } = string.Empty;
    public string? model { get; set; }
    public string scannerMode { get; set; } = "Keyboard";
    public bool enabled { get; set; }
    public string? boundUserId { get; set; }
    public DateTime? lastOnlineTime { get; set; }
    public DateTime createTime { get; set; }
    public DateTime? updateTime { get; set; }
}

public class PdaScanQueryRequest
{
    public string barcode { get; set; } = string.Empty;
    public string scanId { get; set; } = string.Empty;
}

public class PdaScanResultDto
{
    public bool success { get; set; }
    public string resultCode { get; set; } = string.Empty;
    public string message { get; set; } = string.Empty;
    public string businessType { get; set; } = "ProductBarcode";
    public object? data { get; set; }
    public string traceId { get; set; } = string.Empty;
    public DateTime scanTime { get; set; }
}

public class PdaScanLogQueryDto
{
    public int current { get; set; } = 1;
    public int size { get; set; } = 20;
    public string? barcode { get; set; }
    public string? deviceCode { get; set; }
    public string? userId { get; set; }
    public bool? success { get; set; }
    public DateTime? startTime { get; set; }
    public DateTime? endTime { get; set; }
}

public class PdaScanLogDto
{
    public long id { get; set; }
    public string scanId { get; set; } = string.Empty;
    public string barcode { get; set; } = string.Empty;
    public string deviceCode { get; set; } = string.Empty;
    public string userId { get; set; } = string.Empty;
    public string? userName { get; set; }
    public string? businessType { get; set; }
    public bool success { get; set; }
    public string resultCode { get; set; } = string.Empty;
    public string message { get; set; } = string.Empty;
    public string? resultData { get; set; }
    public int durationMs { get; set; }
    public string traceId { get; set; } = string.Empty;
    public DateTime scanTime { get; set; }
}

public class PdaBarcodeRuleQueryDto
{
    public int current { get; set; } = 1;
    public int size { get; set; } = 20;
    public string? keyword { get; set; }
    public bool? enabled { get; set; }
}

public class PdaBarcodeRuleSaveDto
{
    public long? id { get; set; }
    public string ruleCode { get; set; } = string.Empty;
    public string ruleName { get; set; } = string.Empty;
    public string? prefix { get; set; }
    public int? barcodeLength { get; set; }
    public string? pattern { get; set; }
    public string businessType { get; set; } = "ProductBarcode";
    public string handlerCode { get; set; } = "ProductBarcodeQuery";
    public string validationSource { get; set; } = "Local";
    public int priority { get; set; } = 100;
    public bool enabled { get; set; } = true;
}

public class PdaBarcodeRuleDto
{
    public long id { get; set; }
    public string ruleCode { get; set; } = string.Empty;
    public string ruleName { get; set; } = string.Empty;
    public string? prefix { get; set; }
    public int? barcodeLength { get; set; }
    public string? pattern { get; set; }
    public string businessType { get; set; } = string.Empty;
    public string handlerCode { get; set; } = string.Empty;
    public string validationSource { get; set; } = "Local";
    public int priority { get; set; }
    public bool enabled { get; set; }
    public int version { get; set; }
    public DateTime createTime { get; set; }
    public DateTime? updateTime { get; set; }
}

public class PdaPageResult<T>
{
    public List<T> records { get; set; } = new();
    public int current { get; set; }
    public int size { get; set; }
    public int total { get; set; }
}

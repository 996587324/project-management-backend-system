using SqlSugar;

namespace Model.Pda;

[SugarTable("PdaDevice")]
public class PdaDevice
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string DeviceCode { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string DeviceName { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? Model { get; set; }

    [SugarColumn(Length = 32, IsNullable = false)]
    public string ScannerMode { get; set; } = "Keyboard";

    public bool Enabled { get; set; } = true;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? BoundUserId { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastOnlineTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    [SugarColumn(IsNullable = true)]
    public DateTime? UpdateTime { get; set; }
}

[SugarTable("PdaScanLog")]
public class PdaScanLog
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 36, IsNullable = false)]
    public string ScanId { get; set; } = string.Empty;

    [SugarColumn(Length = 256, IsNullable = false)]
    public string Barcode { get; set; } = string.Empty;

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? RawBarcode { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string DeviceCode { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string UserId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UserName { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? BusinessType { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? RuleCode { get; set; }

    public bool Success { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string ResultCode { get; set; } = string.Empty;

    [SugarColumn(Length = 512, IsNullable = false)]
    public string Message { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ResultData { get; set; }

    public int DurationMs { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string TraceId { get; set; } = string.Empty;

    public DateTime ScanTime { get; set; } = DateTime.Now;
}

[SugarTable("PdaBarcodeRule")]
public class PdaBarcodeRule
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string RuleCode { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string RuleName { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? Prefix { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? BarcodeLength { get; set; }

    [SugarColumn(Length = 512, IsNullable = true)]
    public string? Pattern { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string BusinessType { get; set; } = "ProductBarcode";

    [SugarColumn(Length = 64, IsNullable = false)]
    public string HandlerCode { get; set; } = "ProductBarcodeQuery";

    /// <summary>规则校验来源：Local 读取后台规则条件，Remote 调用后端配置的远程校验接口。</summary>
    [SugarColumn(Length = 16, IsNullable = true)]
    public string? ValidationSource { get; set; } = "Local";

    public int Priority { get; set; } = 100;

    public bool Enabled { get; set; } = true;

    public int Version { get; set; } = 1;

    public DateTime CreateTime { get; set; } = DateTime.Now;

    [SugarColumn(IsNullable = true)]
    public DateTime? UpdateTime { get; set; }
}

[SugarTable("PdaLoginLog")]
public class PdaLoginLog
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string UserId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string UserName { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string DeviceCode { get; set; } = string.Empty;

    public bool Success { get; set; }

    [SugarColumn(Length = 256, IsNullable = false)]
    public string Message { get; set; } = string.Empty;

    public DateTime LoginTime { get; set; } = DateTime.Now;
}

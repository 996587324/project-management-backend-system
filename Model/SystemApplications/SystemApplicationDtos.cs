namespace Model.SystemApplications;

public sealed class ApplicationGrantDto
{
    public string UserId { get; set; } = string.Empty;
    public string AppCode { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool UseLegacyPermissions { get; set; }
    public bool AllResources { get; set; }
    public List<string> Permissions { get; set; } = [];
    public List<string> McpScopes { get; set; } = [];
    public List<string> LineCodes { get; set; } = [];
    public List<string> ProjectCodes { get; set; } = [];
    public List<string> DeviceCodes { get; set; } = [];
    public List<string> SourceKeys { get; set; } = [];
}

public sealed class ApplicationTicketRequest
{
    public string AppCode { get; set; } = string.Empty;
    public string CodeChallenge { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

public sealed class ApplicationTicketRedeemRequest
{
    public string AppCode { get; set; } = string.Empty;
    public string Ticket { get; set; } = string.Empty;
    public string CodeVerifier { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

public sealed class ApplicationAccessContext
{
    public string UserId { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string AppCode { get; init; } = string.Empty;
    public bool PlatformAdministrator { get; init; }
    public bool AllResources { get; init; }
    public HashSet<string> Permissions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> McpScopes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> LineCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ProjectCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DeviceCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SourceKeys { get; init; } = new(StringComparer.Ordinal);
    public bool HasPermission(string key) => Permissions.Contains("*") || Permissions.Contains(key);
}

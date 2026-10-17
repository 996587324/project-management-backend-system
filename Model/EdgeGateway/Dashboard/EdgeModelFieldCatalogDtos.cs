namespace Model.EdgeGateway;

public sealed class EdgeModelFieldCatalogQuery
{
    public string ModelMark { get; set; } = string.Empty;
    public string? Keyword { get; set; }
    public bool? Removed { get; set; }
    public DateTime? LastSeenStart { get; set; }
    public DateTime? LastSeenEnd { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

public sealed class EdgeModelFieldCatalogRow
{
    public long Id { get; set; }
    public string ModelMark { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ValueType { get; set; } = string.Empty;
    public bool Removed { get; set; }
    public DateTime FirstSeenTime { get; set; }
    public DateTime LastSeenTime { get; set; }
    public long SeenCount { get; set; }
    public int ReferenceCount { get; set; }
}

public sealed class EdgeModelFieldReference
{
    public string SourcePath { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
}

public sealed class EdgeModelFieldCatalogChange
{
    public string ModelMark { get; set; } = string.Empty;
    public List<long> Ids { get; set; } = [];
    public bool Removed { get; set; }
    public bool Confirm { get; set; }
}

public sealed class EdgeModelFieldCatalogChangeResult
{
    public bool Applied { get; set; }
    public int ChangedCount { get; set; }
    public int ReferenceCount { get; set; }
    public List<EdgeModelFieldReference> References { get; set; } = [];
}

public sealed class EdgeModelFieldCatalogPage
{
    public int Current { get; set; }
    public int Size { get; set; }
    public int Total { get; set; }
    public List<EdgeModelFieldCatalogRow> Records { get; set; } = [];
}

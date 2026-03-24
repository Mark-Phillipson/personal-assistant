internal enum DatabaseProviderType
{
    Unknown,
    SQLite,
    SqlServer
}

internal sealed record DatabaseColumn(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey = false,
    int? MaxLength = null)
{
    public string DisplayType => DataType;
}

internal sealed record DatabaseSource(
    string Alias,
    DatabaseProviderType ProviderType,
    string ConnectionString,
    string DisplayName,
    string? DefaultSchema,
    bool ReadOnly)
{
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? Alias : DisplayName;
}

internal sealed record DatabaseSourceDefinition
{
    public string? Alias { get; init; }
    public string? Provider { get; init; }
    public string? ConnectionString { get; init; }
    public string? DisplayName { get; init; }
    public string? DefaultSchema { get; init; }
    public bool? ReadOnly { get; init; }
}

internal sealed record DatabaseRegistryDefinition
{
    public List<DatabaseSourceDefinition>? Databases { get; init; }
}

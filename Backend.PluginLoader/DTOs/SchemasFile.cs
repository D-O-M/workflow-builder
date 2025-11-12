namespace Backend.PluginLoader.DTOs;

internal record SchemasFile
{
    public required SchemasFileComponents Components { get; set; }
}

internal record SchemasFileComponents
{
    public required Dictionary<string, TypeSchema> Schemas { get; set; }
}

internal record TypeSchema
{
    public required string[] Type { get; set; }
    public required Dictionary<string, TypeInfo> Properties { get; set; }
}

internal record TypeInfo
{
    public required string Type { get; set; }
}

namespace Backend.DTOs;

public record SchemasFile
{
    public required SchemasFileComponents Components { get; set; }
}

public record SchemasFileComponents
{
    public required Dictionary<string, TypeSchema> Schemas { get; set; }
}

public record TypeSchema
{
    public required string[] Type { get; set; }
    public required Dictionary<string, TypeInfo> Properties { get; set; }
}

public record TypeInfo
{
    public required string Type { get; set; }
}

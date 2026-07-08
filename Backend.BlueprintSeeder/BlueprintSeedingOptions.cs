namespace Backend.BlueprintSeeder;

internal sealed class BlueprintSeedingOptions
{
    public const string SectionName = "BlueprintSeeding";

    public bool Enabled { get; init; }
    public string? BlueprintsRoot { get; init; }
    public string? ApiBaseUrl { get; init; }
    public BlueprintSeederAuthenticationOptions Authentication { get; init; } = new();
}

internal sealed class BlueprintSeederAuthenticationOptions
{
    public bool Enabled { get; init; }
    public string? Authority { get; init; }
    public string ClientId { get; init; } = "workflow-builder-blueprint-seeder";
    public string? ClientSecret { get; init; }
    public string? Scope { get; init; }
}

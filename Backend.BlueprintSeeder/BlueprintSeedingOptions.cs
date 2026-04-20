namespace Backend.BlueprintSeeder;

internal sealed class BlueprintSeedingOptions
{
    public const string SectionName = "BlueprintSeeding";

    public bool Enabled { get; init; }
    public string? BlueprintsRoot { get; init; }
    public string? ApiBaseUrl { get; init; }
}

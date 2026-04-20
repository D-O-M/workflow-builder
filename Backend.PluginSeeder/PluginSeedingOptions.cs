namespace Backend.PluginSeeder;

internal sealed class PluginSeedingOptions
{
    public const string SectionName = "PluginSeeding";

    public bool Enabled { get; set; }

    public string? BundlesRoot { get; set; }
}

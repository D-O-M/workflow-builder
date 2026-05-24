namespace ShowcaseTownLookup;

public sealed class TownLookupRequest
{
    public string RequestedTownName { get; set; } = "";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class ResolvedTown
{
    public string RequestedTownName { get; set; } = "";
    public string ResolvedTownName { get; set; } = "";
    public string ResolvedDisplayName { get; set; } = "";
    public string ResolvedCountryCode { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

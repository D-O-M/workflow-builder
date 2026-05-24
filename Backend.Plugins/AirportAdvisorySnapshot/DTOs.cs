namespace AirportAdvisorySnapshot;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class AirportAdvisoryInput
{
    public string AirportCode { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class AirportAdvisoryResult
{
    public string AirportCode { get; set; } = "";
    public int AdvisoryCount { get; set; }
    public int HighestAdvisorySeverityScore { get; set; }
    public string Summary { get; set; } = "";
}

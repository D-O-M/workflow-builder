namespace AirportTafOutlook;

public sealed class AirportTafInput
{
    public string StationCode { get; set; } = "";
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class AirportTafResult
{
    public string StationCode { get; set; } = "";
    public string WorstFlightCategory { get; set; } = "";
    public double ForecastWindSpeedKnots { get; set; }
    public double ForecastVisibilityMiles { get; set; }
    public int ForecastCeilingFeet { get; set; }
    public string Summary { get; set; } = "";
}

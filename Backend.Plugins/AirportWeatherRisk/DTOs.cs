namespace AirportWeatherRisk;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class AirportWeatherRiskInput
{
    public string AirportCode { get; set; } = "";
    public string MetarFlightCategory { get; set; } = "";
    public double MetarWindSpeedKnots { get; set; }
    public double MetarVisibilityMiles { get; set; }
    public int MetarCeilingFeet { get; set; }
    public string TafWorstFlightCategory { get; set; } = "";
    public double TafForecastWindSpeedKnots { get; set; }
    public double TafForecastVisibilityMiles { get; set; }
    public int TafForecastCeilingFeet { get; set; }
    public int AdvisoryCount { get; set; }
    public int HighestAdvisorySeverityScore { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class AirportWeatherRiskResult
{
    public string AirportCode { get; set; } = "";
    public int OperationalWeatherRiskScore { get; set; }
    public string Summary { get; set; } = "";
}

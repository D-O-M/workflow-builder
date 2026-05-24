namespace AirportDisruptionAdvisor;

public sealed class AirportDisruptionAdvisorInput
{
    public string AirportCode { get; set; } = "";
    public string AirportName { get; set; } = "";
    public string WindowLabel { get; set; } = "";
    public int OperationalWeatherRiskScore { get; set; }
    public string WeatherSummary { get; set; } = "";
    public int NearbyAircraftCount { get; set; }
    public int GroundAircraftCount { get; set; }
    public int TrafficPressureScore { get; set; }
    public double AverageDaylightHours { get; set; }
    public string DaylightSummary { get; set; } = "";
    public double SensitivityMultiplier { get; set; }
    public int EscalateThreshold { get; set; }
    public int MonitorThreshold { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class AirportDisruptionAdvisorResult
{
    public string AirportCode { get; set; } = "";
    public string AirportName { get; set; } = "";
    public int DisruptionScore { get; set; }
    public string AdvisoryBand { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
    public string Summary { get; set; } = "";
}

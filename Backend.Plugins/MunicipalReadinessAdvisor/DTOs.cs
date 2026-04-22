namespace MunicipalReadinessAdvisor;

public sealed class MunicipalReadinessInput
{
    public string LocationLabel { get; set; } = "";
    public int WeatherSuitabilityScore { get; set; }
    public string WeatherSummary { get; set; } = "";
    public int HolidayPressureScore { get; set; }
    public string HolidaySummary { get; set; } = "";
    public int DaylightScore { get; set; }
    public string DaylightSummary { get; set; } = "";
    public int AirQualityScore { get; set; }
    public string AirQualitySummary { get; set; } = "";
    public double SensitivityMultiplier { get; set; }
    public int GoThreshold { get; set; }
    public int WatchThreshold { get; set; }
}

public sealed class MunicipalReadinessResult
{
    public string LocationLabel { get; set; } = "";
    public int ReadinessScore { get; set; }
    public string RecommendationBand { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
    public string Summary { get; set; } = "";
}

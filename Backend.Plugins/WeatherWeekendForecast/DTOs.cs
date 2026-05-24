using Backend.Plugins.Harness;

namespace WeatherWeekendForecast;

public sealed class GeoLocation
{
    public string Town { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string CountryCode { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class WeekendForecastScore
{
    public string Town { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateOnly WeekendStart { get; set; }
    public DateOnly WeekendEnd { get; set; }

    public double AvgTemperatureC { get; set; }
    public double MaxWindSpeedMps { get; set; }
    public double TotalPrecipMm { get; set; }
    public double RainRisk01 { get; set; }

    public int WeatherSuitabilityScore { get; set; }

    public string Summary { get; set; } = "";
}

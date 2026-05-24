namespace WindowWeatherOutlook;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class WindowWeatherInput
{
    public string LocationLabel { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class WindowWeatherResult
{
    public string LocationLabel { get; set; } = "";
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
    public double AverageTemperatureC { get; set; }
    public double TotalPrecipitationMm { get; set; }
    public double MaxWindSpeedMps { get; set; }
    public int WeatherRiskScore { get; set; }
    public string Summary { get; set; } = "";
}

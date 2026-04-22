namespace PublicAirQualityOutlook;

public sealed class AirQualityWindowInput
{
    public string LocationLabel { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string WindowStartIsoDate { get; set; } = "";
    public string WindowEndIsoDate { get; set; } = "";
}

public sealed class AirQualityWindowResult
{
    public string LocationLabel { get; set; } = "";
    public string WindowStartIsoDate { get; set; } = "";
    public string WindowEndIsoDate { get; set; } = "";
    public double AverageAirQualityIndex { get; set; }
    public int AirQualityScore { get; set; }
    public string Summary { get; set; } = "";
}

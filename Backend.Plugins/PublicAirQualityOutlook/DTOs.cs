namespace PublicAirQualityOutlook;

public sealed class AirQualityWindowInput
{
    public string LocationLabel { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
}

public sealed class AirQualityWindowResult
{
    public string LocationLabel { get; set; } = "";
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
    public double AverageAirQualityIndex { get; set; }
    public int AirQualityScore { get; set; }
    public string Summary { get; set; } = "";
}

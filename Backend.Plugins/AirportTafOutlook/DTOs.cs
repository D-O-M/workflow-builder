namespace AirportTafOutlook;

public sealed class AirportTafInput
{
    public string StationCode { get; set; } = "";
    public string WindowStartIsoDate { get; set; } = "";
    public string WindowEndIsoDate { get; set; } = "";
}

public sealed class AirportTafResult
{
    public string StationCode { get; set; } = "";
    public string WorstFlightCategory { get; set; } = "";
    public double ForecastWindSpeedKnots { get; set; }
    public double ForecastVisibilityMiles { get; set; }
    public int ForecastCeilingFeet { get; set; }
    public string Summary { get; set; } = "";
}

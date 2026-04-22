namespace AirportMetarSnapshot;

public sealed class AirportMetarInput
{
    public string StationCode { get; set; } = "";
}

public sealed class AirportMetarResult
{
    public string StationCode { get; set; } = "";
    public string FlightCategory { get; set; } = "";
    public double WindSpeedKnots { get; set; }
    public double VisibilityMiles { get; set; }
    public int CeilingFeet { get; set; }
    public string Summary { get; set; } = "";
}

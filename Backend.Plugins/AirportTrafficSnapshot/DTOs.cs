namespace AirportTrafficSnapshot;

public sealed class AirportTrafficInput
{
    public string AirportCode { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public sealed class AirportTrafficResult
{
    public string AirportCode { get; set; } = "";
    public int NearbyAircraftCount { get; set; }
    public int GroundAircraftCount { get; set; }
    public int TrafficPressureScore { get; set; }
    public string Summary { get; set; } = "";
}

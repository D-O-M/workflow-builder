namespace AirportStationLookup;

public sealed class AirportStationLookupInput
{
    public string AirportCode { get; set; } = "";
}

public sealed class AirportStationLookupResult
{
    public string AirportCode { get; set; } = "";
    public string StationCode { get; set; } = "";
    public string AirportName { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string CountryCode { get; set; } = "";
}

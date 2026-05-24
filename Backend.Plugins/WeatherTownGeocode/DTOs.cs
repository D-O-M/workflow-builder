namespace WeatherTownGeocode;

public sealed class TownInput
{
    public string Town { get; set; } = "";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class GeoLocation
{
    public string Town { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string DisplayName { get; set; }
    public string CountryCode { get; set; }
}

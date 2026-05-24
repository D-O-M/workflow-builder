namespace PublicDaylightWindow;

public sealed class DaylightWindowInput
{
    public string LocationLabel { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DaylightWindowResult
{
    public string LocationLabel { get; set; } = "";
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
    public double AverageDaylightHours { get; set; }
    public int DaylightScore { get; set; }
    public string Summary { get; set; } = "";
}

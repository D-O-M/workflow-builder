namespace PublicDaylightWindow;

public sealed class DaylightWindowInput
{
    public string LocationLabel { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string WindowStartIsoDate { get; set; } = "";
    public string WindowEndIsoDate { get; set; } = "";
}

public sealed class DaylightWindowResult
{
    public string LocationLabel { get; set; } = "";
    public string WindowStartIsoDate { get; set; } = "";
    public string WindowEndIsoDate { get; set; } = "";
    public double AverageDaylightHours { get; set; }
    public int DaylightScore { get; set; }
    public string Summary { get; set; } = "";
}

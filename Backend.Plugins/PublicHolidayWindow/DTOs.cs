namespace PublicHolidayWindow;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class HolidayWindowInput
{
    public string LocationLabel { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class HolidayWindowResult
{
    public string LocationLabel { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public DateOnly WindowStartIsoDate { get; set; }
    public DateOnly WindowEndIsoDate { get; set; }
    public int HolidayCount { get; set; }
    public string HeadlineHoliday { get; set; } = "";
    public int HolidayPressureScore { get; set; }
    public string Summary { get; set; } = "";
}

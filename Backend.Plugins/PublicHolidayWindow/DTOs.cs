namespace PublicHolidayWindow;

public sealed class HolidayWindowInput
{
    public string LocationLabel { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string WindowStartIsoDate { get; set; } = "";
    public string WindowEndIsoDate { get; set; } = "";
}

public sealed class HolidayWindowResult
{
    public string LocationLabel { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string WindowStartIsoDate { get; set; } = "";
    public string WindowEndIsoDate { get; set; } = "";
    public int HolidayCount { get; set; }
    public string HeadlineHoliday { get; set; } = "";
    public int HolidayPressureScore { get; set; }
    public string Summary { get; set; } = "";
}

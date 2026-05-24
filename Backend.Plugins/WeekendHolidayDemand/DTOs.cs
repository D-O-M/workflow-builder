using Backend.Plugins.Harness;

namespace WeekendHolidayDemand;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class HolidayDemandInput
{
    public string Town { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public int Year { get; set; }
    public DateOnly WeekendStart { get; set; }
    public DateOnly WeekendEnd { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class HolidayDemandResult
{
    public string Town { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public DateOnly WeekendStart { get; set; }
    public DateOnly WeekendEnd { get; set; }
    public bool HolidayNearby { get; set; }
    public string HolidayName { get; set; }
    public double DemandAdjustment { get; set; }
    public string Summary { get; set; } = "";
}

namespace TownFinalScore;

public sealed class FinalScoreInput
{
    public string Town { get; set; } = "";
    public DateOnly WeekendStart { get; set; }
    public DateOnly WeekendEnd { get; set; }
    public int WeatherSuitabilityScore { get; set; }
    public string Summary { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public bool HolidayNearby { get; set; }
    public string HolidayName { get; set; }
    public double DemandAdjustment { get; set; }
}

public sealed class FinalTownScore
{
    public string Town { get; set; } = "";
    public int WeatherScore { get; set; }
    public double DemandAdjustment { get; set; }
    public int FinalScore { get; set; }
    public string Explanation { get; set; } = "";
    public string ForecastSummary { get; set; } = "";
    public string HolidaySummary { get; set; } = "";
}

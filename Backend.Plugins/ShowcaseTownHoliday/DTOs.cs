namespace ShowcaseTownHoliday;

public sealed class TownWeekendForecast
{
    public string RequestedTownName { get; set; } = "";
    public string ResolvedTownName { get; set; } = "";
    public string ResolvedDisplayName { get; set; } = "";
    public string ResolvedCountryCode { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string WeekendStartIsoDate { get; set; } = "";
    public string WeekendEndIsoDate { get; set; } = "";
    public double AverageTemperatureC { get; set; }
    public double MaximumWindSpeedMps { get; set; }
    public double TotalPrecipitationMm { get; set; }
    public double RainRisk01 { get; set; }
    public int WeatherSuitabilityScore { get; set; }
    public string WeatherSummary { get; set; } = "";
}

public sealed class TownWeekendHolidayContext
{
    public string RequestedTownName { get; set; } = "";
    public string ResolvedTownName { get; set; } = "";
    public string ResolvedDisplayName { get; set; } = "";
    public string ResolvedCountryCode { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string WeekendStartIsoDate { get; set; } = "";
    public string WeekendEndIsoDate { get; set; } = "";
    public double AverageTemperatureC { get; set; }
    public double MaximumWindSpeedMps { get; set; }
    public double TotalPrecipitationMm { get; set; }
    public double RainRisk01 { get; set; }
    public int WeatherSuitabilityScore { get; set; }
    public string WeatherSummary { get; set; } = "";
    public bool HolidayLookupSucceeded { get; set; }
    public bool NearbyHolidayDetected { get; set; }
    public string NearbyHolidayName { get; set; } = "";
    public double HolidayDemandAdjustmentPct { get; set; }
    public string HolidaySummary { get; set; } = "";
}

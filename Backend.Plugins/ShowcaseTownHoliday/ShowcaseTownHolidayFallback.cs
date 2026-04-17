namespace ShowcaseTownHoliday;

public static class ShowcaseTownHolidayFallback
{
    public static TownWeekendHolidayContext CreateUnavailableContext(
        TownWeekendForecast townWeekendForecast,
        string holidaySummary)
    {
        return new TownWeekendHolidayContext
        {
            RequestedTownName = townWeekendForecast.RequestedTownName,
            ResolvedTownName = townWeekendForecast.ResolvedTownName,
            ResolvedDisplayName = townWeekendForecast.ResolvedDisplayName,
            ResolvedCountryCode = townWeekendForecast.ResolvedCountryCode,
            Latitude = townWeekendForecast.Latitude,
            Longitude = townWeekendForecast.Longitude,
            WeekendStartIsoDate = townWeekendForecast.WeekendStartIsoDate,
            WeekendEndIsoDate = townWeekendForecast.WeekendEndIsoDate,
            AverageTemperatureC = townWeekendForecast.AverageTemperatureC,
            MaximumWindSpeedMps = townWeekendForecast.MaximumWindSpeedMps,
            TotalPrecipitationMm = townWeekendForecast.TotalPrecipitationMm,
            RainRisk01 = townWeekendForecast.RainRisk01,
            WeatherSuitabilityScore = townWeekendForecast.WeatherSuitabilityScore,
            WeatherSummary = townWeekendForecast.WeatherSummary,
            HolidayLookupSucceeded = false,
            NearbyHolidayDetected = false,
            NearbyHolidayName = "",
            HolidayDemandAdjustmentPct = 0,
            HolidaySummary = holidaySummary
        };
    }
}

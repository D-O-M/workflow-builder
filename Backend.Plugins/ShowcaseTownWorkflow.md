# Showcase Town Workflow

This demo chain is a simple linear workflow that shows how the app passes typed IO between plugins while making real public API calls.

## Plugin order

1. `Showcase town lookup`
2. `Showcase weekend forecast`
3. `Showcase holiday context`
4. `Showcase town recommendation`

## Trigger payload

Use this as the workflow's first input:

```json
{
  "RequestedTownName": "Porto"
}
```

## Suggested towns

- `Porto`
- `Valencia`
- `Bergen`
- `Reykjavik`

## Channel flow

- `TownLookupRequest -> ResolvedTown`
- `ResolvedTown -> TownWeekendForecast`
- `TownWeekendForecast -> TownWeekendHolidayContext`
- `TownWeekendHolidayContext -> TownRecommendation`

## Final output shape

The last plugin returns a `TownRecommendation` object with:

- `ResolvedTownName`
- `ResolvedCountryCode`
- `WeekendStartIsoDate`
- `WeekendEndIsoDate`
- `WeatherSuitabilityScore`
- `HolidayDemandAdjustmentPct`
- `FinalRecommendationScore`
- `RecommendationBand`
- `RecommendationSummary`
- `WeatherSummary`
- `HolidaySummary`

## Notes

- The holiday plugin soft-fails if the public holiday API is unavailable.
- These plugins are intended for manual demos, not deterministic CI.

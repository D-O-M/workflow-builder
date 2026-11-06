# Showcase Town Weekend Recommendation Blueprint

This four-Step Blueprint passes typed outputs between Plugins while making public API calls. [Seed definition](../SeedData/Blueprints/showcase-town-weekend-recommendation.blueprint.json)

## Plugin order

1. `Showcase town lookup`
2. `Showcase weekend forecast`
3. `Showcase holiday context`
4. `Showcase town recommendation`

## Trigger payload

Use this as the starting Step's input.

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

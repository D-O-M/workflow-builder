# Showcase Municipal Readiness Advisor

## Business Problem

A town-centre or visitor-economy team needs a fast recommendation for whether to scale staffing, comms, and readiness for the next weekend window.

## Start Input

- `Town`: municipality or district to evaluate

## Blueprint Notes

- `WeatherTownGeocode` resolves the town once.
- `WeatherWeekendForecast` computes the weekend weather window.
- `PublicHolidayWindow`, `PublicDaylightWindow`, and `PublicAirQualityOutlook` each receive only the fields they need through explicit property mappings.
- `MunicipalReadinessAdvisor` uses design-time defaults for:
  - `SensitivityMultiplier = 1.0`
  - `GoThreshold = 78`
  - `WatchThreshold = 55`

## Example Runtime Overrides

Apply runtime overrides to the `MunicipalReadinessAdvisor` node when you want to change how aggressively the workflow recommends scaling up:

- `SensitivityMultiplier = 1.12`
- `GoThreshold = 72`
- `WatchThreshold = 50`

This showcase is designed so the final Step demonstrates `runtime override > design-time override > mapped input`.

## Public APIs

- geocoding: Open-Meteo Geocoding API via existing `WeatherTownGeocode`
- forecast: MET Norway Locationforecast via existing `WeatherWeekendForecast`
- holidays: Nager.Date public holidays API
- daylight: sunrise-sunset.org API
- air quality: Open-Meteo Air Quality API

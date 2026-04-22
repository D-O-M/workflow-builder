# Showcase Cross-Border Delivery Window Advisor

## Business Problem

A procurement or logistics team needs a fast recommendation on whether a specific cross-border delivery window is operationally and commercially attractive.

## Start Input

- `ShipmentLabel`
- `DestinationTown`
- `DestinationCountryCode`
- `SupplierCountryCode`
- `ReceiverCountryCode`
- `BaseCurrencyCode`
- `QuoteCurrencyCode`
- `WindowStartIsoDate`
- `WindowEndIsoDate`

## Blueprint Notes

- `DeliveryScenarioNormalizer` creates the shared scenario facts once.
- `WeatherTownGeocode` resolves only the destination location.
- `PublicHolidayWindow`, `PublicDaylightWindow`, and `WindowWeatherOutlook` receive only the properties they need through explicit multi-inbound mappings.
- `DeliveryWindowAdvisor` carries the business thresholds as design-time overrides:
  - `TargetExchangeRate = 0.95`
  - `SensitivityMultiplier = 1.0`
  - `GoThreshold = 72`
  - `WatchThreshold = 50`

## Example Runtime Overrides

If the receiving market becomes more price-sensitive or weather-sensitive, apply runtime overrides to `DeliveryWindowAdvisor` such as:

- `TargetExchangeRate = 0.90`
- `SensitivityMultiplier = 0.95`
- `GoThreshold = 68`

This showcase is designed so the final Step demonstrates `runtime override > design-time override > mapped input`.

## Public APIs

- geocoding: Open-Meteo Geocoding API via existing `WeatherTownGeocode`
- holidays: Nager.Date public holidays API
- daylight: sunrise-sunset.org API
- weather: Open-Meteo Forecast API
- FX: ECB Data API

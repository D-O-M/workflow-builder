# Showcase Airport Disruption Early Warning

## Business Problem

An airport or airline operations team wants an early warning on likely disruption for a selected airport and operating window, using public weather and traffic signals.

## Start Input

- `AirportCode`
- `WindowStartIsoDate`
- `WindowEndIsoDate`

## Blueprint Notes

- `AirportOperationWindow` normalizes the selected operating window.
- `AirportStationLookup` resolves the station and coordinates once.
- `AirportMetarSnapshot`, `AirportTafOutlook`, `AirportAdvisorySnapshot`, and `AirportTrafficSnapshot` each receive only the properties they need.
- `AirportWeatherRisk` is a pure join/scoring Step fed by explicit partial mappings from METAR, TAF, and advisory branches.
- `AirportDisruptionAdvisor` carries design-time defaults for:
  - `SensitivityMultiplier = 1.0`
  - `EscalateThreshold = 70`
  - `MonitorThreshold = 45`

## Example Runtime Overrides

If the ops team wants to surface warnings earlier for a busy day, apply runtime overrides to `AirportDisruptionAdvisor` such as:

- `SensitivityMultiplier = 1.1`
- `EscalateThreshold = 64`
- `MonitorThreshold = 40`

This showcase is designed so the final Step demonstrates `runtime override > design-time override > mapped input`.

## Public APIs

- station, METAR, TAF, SIGMET/advisory data: FAA Aviation Weather Center Data API
- daylight: sunrise-sunset.org API
- traffic snapshot: OpenSky API

## OpenSky Assumption

This showcase assumes approved use of OpenSky under the licensed-use assumption agreed for this work. The `AirportTrafficSnapshot` Step supports an optional `OPENSKY_API_TOKEN` environment variable when authenticated access is available, but it will still attempt the public endpoint when no token is present.

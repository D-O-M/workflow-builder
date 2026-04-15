using Backend.Plugins.Harness;
using WeatherWeekendForecast;

var plugin = Plugin.CreateBuilder<WeekendForecastPlugin>("Weekend forecast scorer")
    .ConfigureInputChannel(p => p.Location)
    .ConfigureOutputChannel(p => p.Forecast)
    .Build();

await plugin.RunAsync();

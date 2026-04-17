using Backend.Plugins.Harness;
using ShowcaseTownForecast;

var plugin = Plugin.CreateBuilder<ShowcaseTownForecastPlugin>("Showcase weekend forecast")
    .ConfigureInputChannel(p => p.ResolvedTown)
    .ConfigureOutputChannel(p => p.TownWeekendForecast)
    .Build();

await plugin.RunAsync();

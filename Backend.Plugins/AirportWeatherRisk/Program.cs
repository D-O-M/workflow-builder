using AirportWeatherRisk;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<AirportWeatherRiskPlugin>("Airport weather risk")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Risk)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using PublicAirQualityOutlook;

var plugin = Plugin.CreateBuilder<PublicAirQualityOutlookPlugin>("Public air quality outlook")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.AirQuality)
    .Build();

await plugin.RunAsync();

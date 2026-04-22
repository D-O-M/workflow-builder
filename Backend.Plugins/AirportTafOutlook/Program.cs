using AirportTafOutlook;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<AirportTafOutlookPlugin>("Airport TAF outlook")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Taf)
    .Build();

await plugin.RunAsync();

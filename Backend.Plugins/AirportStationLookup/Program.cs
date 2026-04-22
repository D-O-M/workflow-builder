using AirportStationLookup;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<AirportStationLookupPlugin>("Airport station lookup")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Station)
    .Build();

await plugin.RunAsync();

using AirportMetarSnapshot;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<AirportMetarSnapshotPlugin>("Airport METAR snapshot")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Metar)
    .Build();

await plugin.RunAsync();

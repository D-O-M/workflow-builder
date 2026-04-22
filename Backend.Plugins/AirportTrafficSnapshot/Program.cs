using AirportTrafficSnapshot;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<AirportTrafficSnapshotPlugin>("Airport traffic snapshot")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Traffic)
    .Build();

await plugin.RunAsync();

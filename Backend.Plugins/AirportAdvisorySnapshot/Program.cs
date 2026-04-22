using AirportAdvisorySnapshot;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<AirportAdvisorySnapshotPlugin>("Airport advisory snapshot")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Advisory)
    .Build();

await plugin.RunAsync();

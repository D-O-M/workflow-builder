using AirportDisruptionAdvisor;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<AirportDisruptionAdvisorPlugin>("Airport disruption advisor")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Advisory)
    .Build();

await plugin.RunAsync();

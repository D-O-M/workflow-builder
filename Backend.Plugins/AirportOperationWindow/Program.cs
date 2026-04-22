using AirportOperationWindow;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<AirportOperationWindowPlugin>("Airport operation window")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Window)
    .Build();

await plugin.RunAsync();

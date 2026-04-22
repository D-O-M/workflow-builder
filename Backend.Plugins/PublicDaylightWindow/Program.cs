using Backend.Plugins.Harness;
using PublicDaylightWindow;

var plugin = Plugin.CreateBuilder<PublicDaylightWindowPlugin>("Public daylight window")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Daylight)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using CiCd.UiBuild;

var plugin = Plugin.CreateBuilder<UiBuildPlugin>("UI build")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

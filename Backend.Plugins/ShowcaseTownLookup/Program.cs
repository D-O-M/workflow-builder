using Backend.Plugins.Harness;
using ShowcaseTownLookup;

var plugin = Plugin.CreateBuilder<ShowcaseTownLookupPlugin>("Showcase town lookup")
    .ConfigureInputChannel(p => p.TownLookupRequest)
    .ConfigureOutputChannel(p => p.ResolvedTown)
    .Build();

await plugin.RunAsync();

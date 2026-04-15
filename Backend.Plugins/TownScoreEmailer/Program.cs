using Backend.Plugins.Harness;
using TownScoreEmailer;

var plugin = Plugin.CreateBuilder<TownEmailPlugin>("Town score emailer")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Sent)
    .Build();

await plugin.RunAsync();

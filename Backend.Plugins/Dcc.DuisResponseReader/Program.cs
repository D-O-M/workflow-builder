using Backend.Plugins.Harness;
using Dcc.DuisResponseReader;

var plugin = Plugin.CreateBuilder<DuisResponseReaderPlugin>("DCC DUIS response reader")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using Dcc.DuisServiceRequestWriter;

var plugin = Plugin.CreateBuilder<DuisServiceRequestWriterPlugin>("DCC DUIS service request writer")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

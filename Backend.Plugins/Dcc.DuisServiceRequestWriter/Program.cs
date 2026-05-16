using Backend.Engine.Capabilities;
using Backend.Plugins.Harness;
using Dcc.DuisServiceRequestWriter;

var plugin = Plugin.CreateBuilder<DuisServiceRequestWriterPlugin>("DCC DUIS service request writer")
    .RequireCapability<DccXmlBlobStorageCapability>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

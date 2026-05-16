using Backend.Engine.Capabilities;
using Backend.Plugins.Harness;
using Dcc.ParseRequestWriter;

var plugin = Plugin.CreateBuilder<ParseRequestWriterPlugin>("DCC parse request writer")
    .RequireCapability<DccXmlBlobStorageCapability>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

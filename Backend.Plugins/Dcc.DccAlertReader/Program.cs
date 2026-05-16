using Backend.Engine.Capabilities;
using Backend.Plugins.Harness;
using Dcc.DccAlertReader;

var plugin = Plugin.CreateBuilder<DccAlertReaderPlugin>("DCC alert reader")
    .RequireCapability<DccXmlBlobStorageCapability>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

using Backend.Engine.Capabilities;
using Backend.Plugins.Harness;
using Dcc.DeviceAlertReader;

var plugin = Plugin.CreateBuilder<DeviceAlertReaderPlugin>("DCC device alert reader")
    .RequireCapability<DccXmlBlobStorageCapability>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

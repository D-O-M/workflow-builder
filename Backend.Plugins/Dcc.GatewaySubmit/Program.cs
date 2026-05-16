using Backend.Engine.Capabilities;
using Backend.Plugins.Harness;
using Dcc.GatewaySubmit;

var plugin = Plugin.CreateBuilder<GatewaySubmitPlugin>("DCC gateway submit")
    .RequireCapability<DccGatewayCapability>()
    .RequireCapability<DccXmlBlobStorageCapability>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

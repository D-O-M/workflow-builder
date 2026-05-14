using Backend.Plugins.Harness;
using Backend.Plugins.Configuration.Dcc;
using Dcc.GatewaySubmit;

var plugin = Plugin.CreateBuilder<GatewaySubmitPlugin>("DCC gateway submit")
    .RequireConfiguration<GatewayOptions>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

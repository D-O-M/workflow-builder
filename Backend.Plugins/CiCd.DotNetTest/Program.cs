using Backend.Engine.Capabilities;
using Backend.Plugins.Harness;
using CiCd.DotNetTest;

var plugin = Plugin.CreateBuilder<DotNetTestPlugin>("Dotnet tests")
    .RequireCapability<DotNetSdkCapability>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

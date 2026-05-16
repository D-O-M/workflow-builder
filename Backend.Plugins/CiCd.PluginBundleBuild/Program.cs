using Backend.Engine.Capabilities;
using Backend.Plugins.Harness;
using CiCd.PluginBundleBuild;

var plugin = Plugin.CreateBuilder<PluginBundleBuildPlugin>("Plugin bundle build")
    .RequireCapability<DotNetSdkCapability>()
    .RequireCapability<PowerShellCapability>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

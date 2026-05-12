using Backend.Plugins.Harness;
using CiCd.PluginBundleBuild;

var plugin = Plugin.CreateBuilder<PluginBundleBuildPlugin>("Plugin bundle build")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

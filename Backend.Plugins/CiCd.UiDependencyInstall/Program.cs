using Backend.Plugins.Harness;
using CiCd.UiDependencyInstall;

var plugin = Plugin.CreateBuilder<UiDependencyInstallPlugin>("UI install")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

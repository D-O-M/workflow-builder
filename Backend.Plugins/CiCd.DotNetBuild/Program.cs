using Backend.Plugins.Harness;
using CiCd.DotNetBuild;

var plugin = Plugin.CreateBuilder<DotNetBuildPlugin>("Dotnet build")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

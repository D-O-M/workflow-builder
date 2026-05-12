using Backend.Plugins.Harness;
using CiCd.DotNetRestore;

var plugin = Plugin.CreateBuilder<DotNetRestorePlugin>("Dotnet restore")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using CiCd.DotNetTest;

var plugin = Plugin.CreateBuilder<DotNetTestPlugin>("Dotnet tests")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

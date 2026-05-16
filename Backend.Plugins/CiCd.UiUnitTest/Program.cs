using Backend.Engine.Capabilities;
using Backend.Plugins.Harness;
using CiCd.UiUnitTest;

var plugin = Plugin.CreateBuilder<UiUnitTestPlugin>("UI unit tests")
    .RequireCapability<NodeJsRuntimeCapability>()
    .RequireCapability<NpmCliCapability>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

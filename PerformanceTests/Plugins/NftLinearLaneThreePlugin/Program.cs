using Backend.Plugins.Harness;
using WorkflowBuilder.PerformanceTests.Plugins;

var plugin = Plugin.CreateBuilder<NftLinearLaneThreePlugin>("NFT linear lane three")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

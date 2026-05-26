using Backend.Plugins.Harness;
using WorkflowBuilder.PerformanceTests.Plugins;

var plugin = Plugin.CreateBuilder<NftJoinPlugin>("NFT fan-in join")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Joined)
    .Build();

await plugin.RunAsync();

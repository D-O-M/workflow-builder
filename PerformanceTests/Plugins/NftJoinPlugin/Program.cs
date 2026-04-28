using Backend.Plugins.Harness;
using WorkflowBuilder.PerformanceTests.Plugins;

var plugin = Plugin.CreateBuilder<NftJoinPlugin>("NFT fan-in join")
    .WithGlobalState(p => p.State)
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Joined)
    .Build();

await plugin.RunAsync();

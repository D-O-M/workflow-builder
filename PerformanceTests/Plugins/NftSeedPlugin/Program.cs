using Backend.Plugins.Harness;
using WorkflowBuilder.PerformanceTests.Plugins;

var plugin = Plugin.CreateBuilder<NftSeedPlugin>("NFT seed context")
    .WithGlobalState(p => p.State)
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Context)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using WorkflowBuilder.PerformanceTests.Plugins;

var plugin = Plugin.CreateBuilder<NftSeedPlugin>("NFT seed context")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Context)
    .Build();

await plugin.RunAsync();

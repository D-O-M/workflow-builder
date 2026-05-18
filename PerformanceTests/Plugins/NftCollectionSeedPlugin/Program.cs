using Backend.Plugins.Harness;
using WorkflowBuilder.PerformanceTests.Plugins;

var plugin = Plugin.CreateBuilder<NftCollectionSeedPlugin>("NFT collection seed")
    .WithGlobalState(p => p.State)
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Collection)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using WorkflowBuilder.PerformanceTests.Plugins;

var plugin = Plugin.CreateBuilder<NftLinearLaneFivePlugin>("NFT linear lane five")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

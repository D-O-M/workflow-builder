using Backend.Plugins.Harness;
using WorkflowBuilder.PerformanceTests.Plugins;

var plugin = Plugin.CreateBuilder<NftBranchGammaPlugin>("NFT gamma branch")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

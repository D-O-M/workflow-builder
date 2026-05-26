using Backend.Plugins.Harness;
using WorkflowBuilder.PerformanceTests.Plugins;

var plugin = Plugin.CreateBuilder<NftSinkPlugin>("NFT completion sink")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Completion)
    .Build();

await plugin.RunAsync();

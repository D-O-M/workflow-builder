using Backend.Plugins.Harness;
using WorkflowBuilder.PerformanceTests.Plugins;

var plugin = Plugin.CreateBuilder<NftItemSinkPlugin>("NFT item sink")
    .WithGlobalState(p => p.State)
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using WorkflowBuilder.PerformanceTests.Plugins;

var plugin = Plugin.CreateBuilder<NftLinearLaneTwoPlugin>("NFT linear lane two")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

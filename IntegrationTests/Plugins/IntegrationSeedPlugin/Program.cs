using Backend.Plugins.Harness;
using WorkflowBuilder.IntegrationTests.Plugins.IntegrationSeedPlugin;

var plugin = Plugin.CreateBuilder<IntegrationSeedPlugin>("Integration Seed Plugin")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Success)
    .WithGlobalState(p => p.State)
    .Build();

await plugin.RunAsync();

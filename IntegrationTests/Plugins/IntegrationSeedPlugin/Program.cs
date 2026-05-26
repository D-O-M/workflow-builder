using Backend.Plugins.Harness;
using WorkflowBuilder.IntegrationTests.Plugins.IntegrationSeedPlugin;

var plugin = Plugin.CreateBuilder<IntegrationSeedPlugin>("Integration Seed Plugin")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Success)
    .Build();

await plugin.RunAsync();

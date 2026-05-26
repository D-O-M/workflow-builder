using Backend.Plugins.Harness;
using WorkflowBuilder.IntegrationTests.Plugins.IntegrationFailurePlugin;

var plugin = Plugin.CreateBuilder<IntegrationFailurePlugin>("Integration Failure Plugin")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Success)
    .Build();

await plugin.RunAsync();

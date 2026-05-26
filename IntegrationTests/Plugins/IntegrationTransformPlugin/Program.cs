using Backend.Plugins.Harness;
using WorkflowBuilder.IntegrationTests.Plugins.IntegrationTransformPlugin;

var plugin = Plugin.CreateBuilder<IntegrationTransformPlugin>("Integration Transform Plugin")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Success)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using WorkflowBuilder.IntegrationTests.Plugins.IntegrationTransformPlugin;

var plugin = Plugin.CreateBuilder<IntegrationTransformPlugin>("Integration Transform Plugin")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Success)
    .WithGlobalState(p => p.State)
    .Build();

await plugin.RunAsync();

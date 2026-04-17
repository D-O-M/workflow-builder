using Backend.Plugins.Harness;
using WorkflowBuilder.IntegrationTests.Plugins.IntegrationResultPlugin;

var plugin = Plugin.CreateBuilder<IntegrationResultPlugin>("Integration Result Plugin")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Success)
    .WithGlobalState(p => p.State)
    .Build();

await plugin.RunAsync();

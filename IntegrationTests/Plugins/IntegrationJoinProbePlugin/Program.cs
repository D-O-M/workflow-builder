using Backend.Plugins.Harness;
using WorkflowBuilder.IntegrationTests.Plugins.IntegrationJoinProbePlugin;

var plugin = Plugin.CreateBuilder<IntegrationJoinProbePlugin>("Integration Join Probe Plugin")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Success)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using WorkflowBuilder.IntegrationTests.Plugins;

var plugin = Plugin.CreateBuilder<IntegrationRecoveryProbePlugin>("Integration Recovery Probe Plugin")
    .ConfigureInputChannel(p => p.Request)
    .ConfigureOutputChannel(p => p.Success)
    .Build();
await plugin.RunAsync();

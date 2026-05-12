using Backend.Plugins.Harness;
using CiCd.DeployArtifactSimulation;

var plugin = Plugin.CreateBuilder<DeployArtifactSimulationPlugin>("Deploy artifact")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

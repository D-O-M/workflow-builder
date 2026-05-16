using Backend.Engine.Capabilities;
using Backend.Plugins.Harness;
using CiCd.DeployArtifactSimulation;

var plugin = Plugin.CreateBuilder<DeployArtifactSimulationPlugin>("Deploy artifact")
    .RequireCapability<CiCdArtifactStorageCapability>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

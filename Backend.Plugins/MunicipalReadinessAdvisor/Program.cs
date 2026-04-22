using Backend.Plugins.Harness;
using MunicipalReadinessAdvisor;

var plugin = Plugin.CreateBuilder<MunicipalReadinessAdvisorPlugin>("Municipal readiness advisor")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Recommendation)
    .Build();

await plugin.RunAsync();

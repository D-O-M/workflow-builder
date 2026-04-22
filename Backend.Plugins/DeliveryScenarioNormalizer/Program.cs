using Backend.Plugins.Harness;
using DeliveryScenarioNormalizer;

var plugin = Plugin.CreateBuilder<DeliveryScenarioNormalizerPlugin>("Delivery scenario normalizer")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Scenario)
    .Build();

await plugin.RunAsync();

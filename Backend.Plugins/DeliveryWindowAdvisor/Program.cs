using Backend.Plugins.Harness;
using DeliveryWindowAdvisor;

var plugin = Plugin.CreateBuilder<DeliveryWindowAdvisorPlugin>("Delivery window advisor")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Decision)
    .Build();

await plugin.RunAsync();

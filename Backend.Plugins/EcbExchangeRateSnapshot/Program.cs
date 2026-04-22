using Backend.Plugins.Harness;
using EcbExchangeRateSnapshot;

var plugin = Plugin.CreateBuilder<EcbExchangeRateSnapshotPlugin>("ECB exchange rate snapshot")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Rate)
    .Build();

await plugin.RunAsync();

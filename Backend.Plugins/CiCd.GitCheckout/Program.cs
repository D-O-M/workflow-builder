using Backend.Plugins.Harness;
using CiCd.GitCheckout;

var plugin = Plugin.CreateBuilder<GitCheckoutPlugin>("Git checkout")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using Dcc.DccAlertReader;

var plugin = Plugin.CreateBuilder<DccAlertReaderPlugin>("DCC alert reader")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

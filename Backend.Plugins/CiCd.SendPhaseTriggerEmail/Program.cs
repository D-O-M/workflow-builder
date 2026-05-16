using Backend.Engine.Capabilities;
using Backend.Plugins.Harness;
using CiCd.SendPhaseTriggerEmail;

var plugin = Plugin.CreateBuilder<SendPhaseTriggerEmailPlugin>("Send approval email")
    .RequireCapability<CiCdPhaseTriggerEmailCapability>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Configuration.CiCd;
using Backend.Plugins.Harness;
using CiCd.SendPhaseTriggerEmail;

var plugin = Plugin.CreateBuilder<SendPhaseTriggerEmailPlugin>("Send approval email")
    .RequireConfiguration<PhaseTriggerEmailOptions>()
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using CiCd.PublishArtifacts;

var plugin = Plugin.CreateBuilder<PublishArtifactsPlugin>("Publish artifacts")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

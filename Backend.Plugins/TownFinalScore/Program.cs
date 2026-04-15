using Backend.Plugins.Harness;
using TownFinalScore;

var plugin = Plugin.CreateBuilder<FinalScorePlugin>("Town final score")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Result)
    .Build();

await plugin.RunAsync();

using Backend.Plugins.Harness;
using ShowcaseTownRecommendation;

var plugin = Plugin.CreateBuilder<ShowcaseTownRecommendationPlugin>("Showcase town recommendation")
    .ConfigureInputChannel(p => p.TownWeekendHolidayContext)
    .ConfigureOutputChannel(p => p.TownRecommendation)
    .Build();

await plugin.RunAsync();

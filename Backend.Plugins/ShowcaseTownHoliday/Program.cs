using Backend.Plugins.Harness;
using ShowcaseTownHoliday;

var plugin = Plugin.CreateBuilder<ShowcaseTownHolidayPlugin>("Showcase holiday context")
    .ConfigureInputChannel(p => p.TownWeekendForecast)
    .ConfigureOutputChannel(p => p.TownWeekendHolidayContext)
    .Build();

await plugin.RunAsync();

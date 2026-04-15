using Backend.Plugins.Harness;
using WeekendHolidayDemand;

var plugin = Plugin.CreateBuilder<HolidayDemandPlugin>("Holiday demand adjuster")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Demand)
    .Build();

await plugin.RunAsync();

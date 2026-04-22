using Backend.Plugins.Harness;
using PublicHolidayWindow;

var plugin = Plugin.CreateBuilder<PublicHolidayWindowPlugin>("Public holiday window")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.WindowHoliday)
    .Build();

await plugin.RunAsync();

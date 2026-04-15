using Backend.Plugins.Harness;
using WeatherTownGeocode;

var plugin = Plugin.CreateBuilder<TownGeocodePlugin>("Town geocoder")
    .ConfigureInputChannel(p => p.Input)
    .ConfigureOutputChannel(p => p.Location)
    .Build();

await plugin.RunAsync();

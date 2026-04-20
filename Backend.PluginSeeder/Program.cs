using Azure.Storage.Blobs;
using Backend.PluginLoader.Services;
using Backend.PluginSeeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

builder.Services.Configure<PluginSeedingOptions>(
    builder.Configuration.GetSection(PluginSeedingOptions.SectionName));

builder.Services.AddSingleton(_ => new BlobContainerClient(
    LocalDevelopmentResourceSettings.GetBlobConnectionString(
        builder.Configuration,
        builder.Environment),
    LocalDevelopmentResourceSettings.GetPluginsBlobContainerName(
        builder.Configuration,
        builder.Environment)));
builder.Services.AddSingleton<Backend.Interfaces.Utilities.IPluginBlobNameParsingUtility, PluginBlobNameParsingUtility>();
builder.Services.AddHostedService<PluginSeedingHostedService>();

await builder.Build().RunAsync();

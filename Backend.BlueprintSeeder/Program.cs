using Backend.BlueprintSeeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

builder.Services.Configure<BlueprintSeedingOptions>(
    builder.Configuration.GetSection(BlueprintSeedingOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<BlueprintSeederAccessTokenProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddTransient<BlueprintSeederAuthenticationHandler>();

builder.Services.AddHttpClient<BlueprintImportClient>((serviceProvider, client) =>
{
    var options = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<BlueprintSeedingOptions>>()
        .Value;

    if (string.IsNullOrWhiteSpace(options.ApiBaseUrl))
    {
        throw new InvalidOperationException("Blueprint seeding API base URL is not configured.");
    }

    client.BaseAddress = new Uri(EnsureTrailingSlash(options.ApiBaseUrl), UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
})
    .AddHttpMessageHandler<BlueprintSeederAuthenticationHandler>();
builder.Services.AddHostedService<BlueprintSeedingHostedService>();

await builder.Build().RunAsync();

static string EnsureTrailingSlash(string baseUrl) =>
    baseUrl.EndsWith("/", StringComparison.Ordinal)
        ? baseUrl
        : $"{baseUrl}/";

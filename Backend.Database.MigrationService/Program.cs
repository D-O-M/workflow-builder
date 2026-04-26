using Backend.Database.MigrationService;
using Backend.Database.Relational;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(DatabaseMigrationWorker.ActivitySourceName));

var sqlConnectionString = LocalDevelopmentResourceSettings.GetTasksDbConnectionString(
    builder.Configuration,
    builder.Environment);
var cosmosConnectionString = LocalDevelopmentResourceSettings.GetCosmosConnectionString(
    builder.Configuration,
    builder.Environment);
Console.WriteLine(
    $"Database migration service using Cosmos endpoint {GetConnectionStringSegment(cosmosConnectionString, "AccountEndpoint") ?? "<unknown>"}.");

builder.Services.AddDbContextPool<BlueprintDbContext>(options =>
    options.UseSqlServer(sqlConnectionString));
builder.Services.AddSingleton(_ => new CosmosClient(
    cosmosConnectionString,
    new CosmosClientOptions
    {
        ConnectionMode = ConnectionMode.Gateway,
        LimitToEndpoint = builder.Environment.IsDevelopment(),
        RequestTimeout = TimeSpan.FromSeconds(10)
    }));
builder.Services.AddHostedService<DatabaseMigrationWorker>();

var host = builder.Build();
host.Run();

static string? GetConnectionStringSegment(string connectionString, string segmentName)
{
    foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var separatorIndex = segment.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = segment[..separatorIndex];
        if (!key.Equals(segmentName, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var value = segment[(separatorIndex + 1)..];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    return null;
}

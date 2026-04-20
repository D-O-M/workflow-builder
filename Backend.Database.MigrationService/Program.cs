using Backend.Database.MigrationService;
using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(DatabaseMigrationWorker.ActivitySourceName));

var sqlConnectionString = LocalDevelopmentResourceSettings.GetTasksDbConnectionString(
    builder.Configuration,
    builder.Environment);

builder.Services.AddDbContextPool<BlueprintDbContext>(options =>
    options.UseSqlServer(sqlConnectionString));
builder.Services.AddHostedService<DatabaseMigrationWorker>();

var host = builder.Build();
host.Run();

using System.Diagnostics;
using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore;

namespace Backend.Database.MigrationService;

internal sealed class DatabaseMigrationWorker(
    IServiceProvider serviceProvider,
    IHostApplicationLifetime hostApplicationLifetime,
    ILogger<DatabaseMigrationWorker> logger) : BackgroundService
{
    public const string ActivitySourceName = "WorkflowBuilder.Database.Migrations";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Apply blueprint database migrations", ActivityKind.Client);

        try
        {
            logger.LogInformation("Applying blueprint database migrations.");

            await using var scope = serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<BlueprintDbContext>();

            var executionStrategy = dbContext.Database.CreateExecutionStrategy();
            await executionStrategy.ExecuteAsync(async () =>
            {
                await dbContext.Database.MigrateAsync(stoppingToken);
            });

            logger.LogInformation("Blueprint database migrations completed successfully.");
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            logger.LogError(exception, "Applying blueprint database migrations failed.");
            throw;
        }
        finally
        {
            hostApplicationLifetime.StopApplication();
        }
    }
}

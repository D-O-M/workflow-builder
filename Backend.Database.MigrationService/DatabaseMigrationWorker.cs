using System.Diagnostics;
using Backend.Database.Relational;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;

namespace Backend.Database.MigrationService;

internal sealed class DatabaseMigrationWorker(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    IHostApplicationLifetime hostApplicationLifetime,
    ILogger<DatabaseMigrationWorker> logger) : BackgroundService
{
    public const string ActivitySourceName = "WorkflowBuilder.Database.Migrations";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly TimeSpan CosmosOperationTimeout = TimeSpan.FromSeconds(45);

    private const string DefaultDatabaseName = "enginedb";
    private const string ProcessesContainerName = "processes";
    private const string ProcessStepsContainerName = "processsteps";
    private const string NodeActivationsContainerName = "nodeactivations";
    private const string NodeJoinStatesContainerName = "nodejoinstates";
    private const string ProcessStepProgressLeasesContainerName = "processstepprogressleases";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Provision workflow databases", ActivityKind.Client);

        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();

            await ApplyRelationalMigrationsAsync(scope.ServiceProvider, stoppingToken);
            await ProvisionProcessStateAsync(scope.ServiceProvider, stoppingToken);

            logger.LogInformation("Workflow database provisioning completed successfully.");
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            logger.LogError(exception, "Workflow database provisioning failed.");
            throw;
        }
        finally
        {
            hostApplicationLifetime.StopApplication();
        }
    }

    private static async Task ApplyRelationalMigrationsAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<DatabaseMigrationWorker>>();
        logger.LogInformation("Applying blueprint database migrations.");

        var dbContext = serviceProvider.GetRequiredService<BlueprintDbContext>();
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        await executionStrategy.ExecuteAsync(async () =>
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
        });

        logger.LogInformation("Blueprint database migrations completed successfully.");
    }

    private async Task ProvisionProcessStateAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var databaseName = configuration["AzureCosmos:DatabaseName"] ?? DefaultDatabaseName;
        var cosmosClient = serviceProvider.GetRequiredService<CosmosClient>();

        logger.LogInformation(
            "Provisioning Cosmos process state database {DatabaseName}.",
            databaseName);

        var databaseResponse = await WithCosmosTimeout(
            cosmosClient.CreateDatabaseIfNotExistsAsync(
                databaseName,
                cancellationToken: cancellationToken),
            "create process state database",
            cancellationToken);

        await EnsureContainerAsync(
            databaseResponse.Database,
            ProcessesContainerName,
            "/blueprintId",
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            ProcessStepsContainerName,
            "/processId",
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            NodeActivationsContainerName,
            "/processId",
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            NodeJoinStatesContainerName,
            "/processId",
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            ProcessStepProgressLeasesContainerName,
            "/id",
            cancellationToken);

        logger.LogInformation(
            "Cosmos process state database {DatabaseName} is provisioned.",
            databaseName);
    }

    private Task<ContainerResponse> EnsureContainerAsync(
        Microsoft.Azure.Cosmos.Database database,
        string containerName,
        string partitionKeyPath,
        CancellationToken cancellationToken) =>
        WithCosmosTimeout(
            database.CreateContainerIfNotExistsAsync(
                new ContainerProperties(containerName, partitionKeyPath),
                cancellationToken: cancellationToken),
            $"create process state container {containerName}",
            cancellationToken);

    private static async Task<T> WithCosmosTimeout<T>(
        Task<T> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.WaitAsync(CosmosOperationTimeout, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Cosmos provisioning operation '{operationName}' did not complete within {CosmosOperationTimeout}.",
                exception);
        }
    }
}

using System.Diagnostics;
using System.Text.Json;
using Backend.Database.Relational;
using Backend.Database.State.Objects;
using Backend.Repositories;
using Backend.Repositories.Processes;
using Backend.Topology;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Provision workflow databases", ActivityKind.Client);

        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();

            await ApplyRelationalMigrationsAsync(scope.ServiceProvider, stoppingToken);
            await ProvisionProcessStateAsync(scope.ServiceProvider, stoppingToken);
            await BackfillProcessIndexAsync(scope.ServiceProvider, stoppingToken);

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
        var databaseName = configuration[WorkflowBuilderTopology.Cosmos.DatabaseNameKey]
            ?? WorkflowBuilderTopology.Cosmos.DefaultDatabaseName;
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
            WorkflowBuilderTopology.Cosmos.Containers.Processes,
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            WorkflowBuilderTopology.Cosmos.Containers.ProcessDirectory,
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            WorkflowBuilderTopology.Cosmos.Containers.ProcessSummaries,
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            WorkflowBuilderTopology.Cosmos.Containers.ProcessSteps,
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            WorkflowBuilderTopology.Cosmos.Containers.ProcessStepFinalizations,
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            WorkflowBuilderTopology.Cosmos.Containers.NodeActivations,
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            WorkflowBuilderTopology.Cosmos.Containers.NodeJoinStates,
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            WorkflowBuilderTopology.Cosmos.Containers.PhasedProcesses,
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            WorkflowBuilderTopology.Cosmos.Containers.PhaseCorrelations,
            cancellationToken);
        await EnsureContainerAsync(
            databaseResponse.Database,
            WorkflowBuilderTopology.Cosmos.Containers.ProcessStepProgressLeases,
            cancellationToken);

        logger.LogInformation(
            "Cosmos process state database {DatabaseName} is provisioned.",
            databaseName);
    }

    private async Task BackfillProcessIndexAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var enabled = configuration.GetValue("ProcessIndexBackfill:Enabled", false);
        if (!enabled)
        {
            logger.LogInformation("Process index backfill is disabled.");
            return;
        }

        var batchSize = Math.Clamp(configuration.GetValue("ProcessIndexBackfill:BatchSize", 100), 1, 500);
        var databaseName = configuration[WorkflowBuilderTopology.Cosmos.DatabaseNameKey]
            ?? WorkflowBuilderTopology.Cosmos.DefaultDatabaseName;
        var cosmosClient = serviceProvider.GetRequiredService<CosmosClient>();
        var database = cosmosClient.GetDatabase(databaseName);
        var processRepository = new ProcessInstanceRepository<Dictionary<string, JsonElement>>(
            database.GetContainer(WorkflowBuilderTopology.Cosmos.Containers.Processes.Name));
        var indexProjectionService = new ProcessIndexProjectionService(
            new ProcessDirectoryRepository(database.GetContainer(WorkflowBuilderTopology.Cosmos.Containers.ProcessDirectory.Name)),
            new ProcessSummaryProjectionRepository(database.GetContainer(WorkflowBuilderTopology.Cosmos.Containers.ProcessSummaries.Name)),
            serviceProvider.GetService<ILogger<ProcessIndexProjectionService>>());

        var dbContext = serviceProvider.GetRequiredService<BlueprintDbContext>();
        var versions = await dbContext.BlueprintVersions
            .AsNoTracking()
            .Select(version => new
            {
                BlueprintVersionId = version.Id,
                version.BlueprintDefinitionId,
            })
            .ToListAsync(cancellationToken);

        logger.LogInformation(
            "Starting process index backfill for {BlueprintVersionCount} blueprint versions with batch size {BatchSize}.",
            versions.Count,
            batchSize);

        var indexedCount = 0;
        foreach (var version in versions)
        {
            string? continuationToken = null;
            do
            {
                var page = await processRepository.GetProcessStateSummaryPageForBlueprint(
                    version.BlueprintVersionId,
                    continuationToken,
                    batchSize,
                    cancellationToken);

                foreach (var summary in page.Items)
                {
                    var process = new ProcessInstanceRecord<Dictionary<string, JsonElement>>
                    {
                        id = summary.ProcessId,
                        BlueprintId = summary.BlueprintId,
                        CreatedAt = summary.CreatedAt,
                        UpdatedAt = summary.UpdatedAt,
                        Status = summary.Status,
                        GlobalState = [],
                    };

                    await indexProjectionService.UpsertFromProcessAsync(
                        process,
                        version.BlueprintDefinitionId,
                        version.BlueprintVersionId,
                        cancellationToken);

                    indexedCount++;
                }

                continuationToken = page.ContinuationToken;
            }
            while (!string.IsNullOrWhiteSpace(continuationToken));
        }

        logger.LogInformation(
            "Process index backfill completed after indexing {IndexedProcessCount} processes.",
            indexedCount);
    }

    private Task<ContainerResponse> EnsureContainerAsync(
        Microsoft.Azure.Cosmos.Database database,
        CosmosContainer container,
        CancellationToken cancellationToken) =>
        WithCosmosTimeout(
            database.CreateContainerIfNotExistsAsync(
                new ContainerProperties(container.Name, container.PartitionKey),
                cancellationToken: cancellationToken),
            $"create process state container {container.Name}",
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

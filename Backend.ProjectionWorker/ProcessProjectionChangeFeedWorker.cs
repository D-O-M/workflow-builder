using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Topology;
using Microsoft.Azure.Cosmos;

namespace Backend.ProjectionWorker;

public sealed class ProcessProjectionChangeFeedWorker : IHostedService
{
    private readonly CosmosClient _cosmosClient;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProjectionWorkerTelemetry _telemetry;
    private readonly ILogger<ProcessProjectionChangeFeedWorker> _logger;
    private ChangeFeedProcessor? _processProcessor;
    private ChangeFeedProcessor? _stepProcessor;

    public ProcessProjectionChangeFeedWorker(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ProjectionWorkerTelemetry telemetry,
        ILogger<ProcessProjectionChangeFeedWorker> logger)
    {
        _cosmosClient = cosmosClient;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var databaseName = _configuration[WorkflowBuilderTopology.Cosmos.DatabaseNameKey]!;
        var database = _cosmosClient.GetDatabase(databaseName);
        var leaseContainer = database.GetContainer(
            WorkflowBuilderTopology.Cosmos.Containers.ProcessProjectionLeases.Name);

        _processProcessor = database
            .GetContainer(WorkflowBuilderTopology.Cosmos.Containers.Processes.Name)
            .GetChangeFeedProcessorBuilder<ProcessInstanceRecord>(
                processorName: "WorkflowBuilder.ProjectionWorker.Processes",
                onChangesDelegate: HandleProcessChangesAsync)
            .WithInstanceName($"{Environment.MachineName}-{Guid.NewGuid():N}")
            .WithLeaseContainer(leaseContainer)
            .WithPollInterval(TimeSpan.FromSeconds(1))
            .WithErrorNotification((leaseToken, exception) =>
                HandleErrorAsync("processes", leaseToken, exception))
            .Build();

        _stepProcessor = database
            .GetContainer(WorkflowBuilderTopology.Cosmos.Containers.ProcessSteps.Name)
            .GetChangeFeedProcessorBuilder<ProcessStepRecord>(
                processorName: "WorkflowBuilder.ProjectionWorker.ProcessSteps",
                onChangesDelegate: HandleStepChangesAsync)
            .WithInstanceName($"{Environment.MachineName}-{Guid.NewGuid():N}")
            .WithLeaseContainer(leaseContainer)
            .WithPollInterval(TimeSpan.FromSeconds(1))
            .WithErrorNotification((leaseToken, exception) =>
                HandleErrorAsync("processsteps", leaseToken, exception))
            .Build();

        await _processProcessor.StartAsync();
        await _stepProcessor.StartAsync();

        _logger.LogInformation(
            "Started projection change-feed processors for {DatabaseName}.",
            databaseName);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stepProcessor is not null)
        {
            await _stepProcessor.StopAsync();
        }

        if (_processProcessor is not null)
        {
            await _processProcessor.StopAsync();
        }

        _logger.LogInformation("Stopped projection change-feed processors.");
    }

    private async Task HandleProcessChangesAsync(
        ChangeFeedProcessorContext context,
        IReadOnlyCollection<ProcessInstanceRecord> changes,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ProcessProjectionChangeHandler>();
        await handler.ProjectProcessChangesAsync(changes, cancellationToken);
    }

    private async Task HandleStepChangesAsync(
        ChangeFeedProcessorContext context,
        IReadOnlyCollection<ProcessStepRecord> changes,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ProcessProjectionChangeHandler>();
        await handler.ProjectStepChangesAsync(changes, cancellationToken);
    }

    private Task HandleErrorAsync(
        string source,
        string leaseToken,
        Exception exception)
    {
        _telemetry.RecordFailure(source, exception);
        _logger.LogWarning(
            exception,
            "Projection worker failed while processing {Source} lease {LeaseToken}.",
            source,
            leaseToken);

        return Task.CompletedTask;
    }
}

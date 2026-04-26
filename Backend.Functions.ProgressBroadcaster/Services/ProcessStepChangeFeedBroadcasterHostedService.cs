using Backend.Database.State.Objects;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Functions.ProgressBroadcaster.Services;

public sealed class ProcessStepChangeFeedBroadcasterHostedService : IHostedService
{
    private readonly CosmosClient _cosmosClient;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProcessStepChangeFeedBroadcasterHostedService> _logger;
    private ChangeFeedProcessor? _processor;

    public ProcessStepChangeFeedBroadcasterHostedService(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<ProcessStepChangeFeedBroadcasterHostedService> logger)
    {
        _cosmosClient = cosmosClient;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var databaseName = _configuration["Realtime:Cosmos:DatabaseName"] ?? "enginedb";
        var tasksContainerName = _configuration["Realtime:Cosmos:TasksContainerName"] ?? "processsteps";
        var leaseContainerName = _configuration["Realtime:Cosmos:LeaseContainerName"] ?? "processstepprogressleases";

        var database = _cosmosClient.GetDatabase(databaseName);
        var monitoredContainer = database.GetContainer(tasksContainerName);
        var leaseContainer = database.GetContainer(leaseContainerName);

        _processor = monitoredContainer
            .GetChangeFeedProcessorBuilder<ProcessStepRecord>(
                processorName: "WorkflowBuilder.ProgressBroadcaster.Hosted",
                onChangesDelegate: HandleChangesAsync)
            .WithInstanceName($"{Environment.MachineName}-{Guid.NewGuid():N}")
            .WithLeaseContainer(leaseContainer)
            .WithPollInterval(TimeSpan.FromSeconds(1))
            .WithErrorNotification(HandleErrorAsync)
            .Build();

        await _processor.StartAsync();

        _logger.LogInformation(
            "Started hosted process-step change feed broadcaster for {DatabaseName}/{ContainerName}.",
            databaseName,
            tasksContainerName);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is null)
        {
            return;
        }

        await _processor.StopAsync();
        _logger.LogInformation("Stopped hosted process-step change feed broadcaster.");
    }

    private async Task HandleChangesAsync(
        ChangeFeedProcessorContext context,
        IReadOnlyCollection<ProcessStepRecord> changes,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        _logger.LogDebug(
            "Hosted process-step change feed broadcaster received {Count} change(s) for lease {LeaseToken}.",
            changes.Count,
            context.LeaseToken);

        using var scope = _scopeFactory.CreateScope();
        var broadcaster = scope.ServiceProvider.GetRequiredService<ProcessProgressBroadcaster>();
        await broadcaster.BroadcastAsync(changes, cancellationToken);
    }

    private Task HandleErrorAsync(string leaseToken, Exception exception)
    {
        _logger.LogWarning(
            exception,
            "Hosted process-step change feed broadcaster failed while processing lease {LeaseToken}.",
            leaseToken);

        return Task.CompletedTask;
    }
}

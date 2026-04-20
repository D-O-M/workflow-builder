using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Repositories;

public sealed class WorkflowExecutionStateInitializerHostedService : IHostedService
{
    private readonly CosmosClient _cosmosClient;
    private readonly WorkflowExecutionStateInitializationOptions _options;
    private readonly ILogger<WorkflowExecutionStateInitializerHostedService> _logger;

    public WorkflowExecutionStateInitializerHostedService(
        CosmosClient cosmosClient,
        WorkflowExecutionStateInitializationOptions options,
        ILogger<WorkflowExecutionStateInitializerHostedService> logger)
    {
        _cosmosClient = cosmosClient;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Ensuring Cosmos workflow execution state exists for database {DatabaseName}.",
            _options.DatabaseName);

        var databaseResponse = await _cosmosClient.CreateDatabaseIfNotExistsAsync(
            _options.DatabaseName,
            cancellationToken: cancellationToken);

        await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(_options.WorkflowsContainerName, "/blueprintId"),
            cancellationToken: cancellationToken);

        await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(_options.TasksContainerName, "/workflowId"),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Cosmos workflow execution state is ready for database {DatabaseName}.",
            _options.DatabaseName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

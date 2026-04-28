using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Repositories;

public sealed class ProcessStateInitializerHostedService : IHostedService
{
    private readonly CosmosClient _cosmosClient;
    private readonly ProcessStateInitializationOptions _options;
    private readonly ILogger<ProcessStateInitializerHostedService> _logger;

    public ProcessStateInitializerHostedService(
        CosmosClient cosmosClient,
        ProcessStateInitializationOptions options,
        ILogger<ProcessStateInitializerHostedService> logger)
    {
        _cosmosClient = cosmosClient;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Ensuring Cosmos process state exists for database {DatabaseName}.",
            _options.DatabaseName);

        var databaseResponse = await _cosmosClient.CreateDatabaseIfNotExistsAsync(
            _options.DatabaseName,
            cancellationToken: cancellationToken);

        await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(_options.ProcessesContainerName, "/blueprintId"),
            cancellationToken: cancellationToken);

        await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(_options.TasksContainerName, "/processId"),
            cancellationToken: cancellationToken);

        await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(_options.ActivationsContainerName, "/processId"),
            cancellationToken: cancellationToken);

        await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(_options.JoinStatesContainerName, "/processId"),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Cosmos process state is ready for database {DatabaseName}.",
            _options.DatabaseName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

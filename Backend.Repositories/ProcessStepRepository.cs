using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;
using System.Net;
using System.Text.Json;

namespace Backend.Repositories;

public class ProcessStepRepository
    : CosmosRepository<ProcessStepRecord>,
    IProcessStepRepository
{
    public ProcessStepRepository(Container container)
        : base(container, wo => wo.Id, wo => new PartitionKey(wo.ProcessId))
    { }

    public async Task<ProcessStepRecord> GetProcessStepById(
        Guid taskInstanceId,
        Guid processId,
        CancellationToken cancellationToken)
    {
        var taskInstance = await GetAsync(
            taskInstanceId.ToString(),
            new PartitionKey(processId.ToString()),
            cancellationToken);

        if (taskInstance is null)
        {
            throw new EntityNotFoundException<ProcessStepRecord>(
                taskInstanceId.ToString());
        }

        return taskInstance;
    }

    public async Task<ProcessStepRecord> CreateProcessStep(
        ProcessStepRecord state,
        CancellationToken cancellationToken)
    {
        return (await CreateAsync(state, cancellationToken)).Resource;
    }

    public async Task<(bool Created, ProcessStepRecord State)> CreateProcessStepIfNotExists(
        ProcessStepRecord state,
        CancellationToken cancellationToken)
    {
        try
        {
            return (true, await CreateProcessStep(state, cancellationToken));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await GetProcessStepById(
                Guid.Parse(state.Id),
                Guid.Parse(state.ProcessId),
                cancellationToken);
            return (false, existing);
        }
    }

    public async Task<IReadOnlyList<(bool Created, ProcessStepRecord State)>> CreateProcessStepsIfNotExists(
        IReadOnlyCollection<ProcessStepRecord> states,
        CancellationToken cancellationToken)
    {
        if (states.Count == 0)
        {
            return [];
        }

        var results = new List<(bool Created, ProcessStepRecord State)>(states.Count);
        foreach (var partitionGroup in states.GroupBy(state => state.ProcessId, StringComparer.Ordinal))
        {
            foreach (var chunk in partitionGroup.Chunk(100))
            {
                var batch = Container.CreateTransactionalBatch(new PartitionKey(partitionGroup.Key));
                foreach (var state in chunk)
                {
                    batch.CreateItem(state);
                }

                using var response = await batch.ExecuteAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    for (var index = 0; index < chunk.Length; index++)
                    {
                        var resource = response.GetOperationResultAtIndex<ProcessStepRecord>(index).Resource;
                        results.Add((true, resource));
                    }

                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                {
                    foreach (var state in chunk)
                    {
                        results.Add(await CreateProcessStepIfNotExists(state, cancellationToken));
                    }

                    continue;
                }

                throw new InvalidOperationException(
                    $"Could not create process steps in a Cosmos transactional batch. Status code: {response.StatusCode}.");
            }
        }

        return results;
    }

    public async Task<(bool Claimed, ProcessStepRecord? State)> TryClaimProcessStepAsync(
        Guid taskInstanceId,
        Guid processId,
        int taskDefinitionId,
        int allowedMajor,
        int allowedMinor,
        int resolvedTaskVersionId,
        string resolvedTaskVersion,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var operations = new List<PatchOperation>
        {
            PatchOperation.Set("/status", ProcessStepStatus.InProgress.ToString()),
            PatchOperation.Set("/taskDefinitionId", taskDefinitionId),
            PatchOperation.Set("/allowedMajor", allowedMajor),
            PatchOperation.Set("/allowedMinor", allowedMinor),
            PatchOperation.Set("/resolvedTaskVersionId", resolvedTaskVersionId),
            PatchOperation.Set("/resolvedTaskVersion", resolvedTaskVersion),
            PatchOperation.Set("/updatedAt", startedAt),
            PatchOperation.Set("/startedAt", startedAt)
        };

        try
        {
            var response = await Container.PatchItemAsync<ProcessStepRecord>(
                taskInstanceId.ToString(),
                new PartitionKey(processId.ToString()),
                operations,
                new PatchItemRequestOptions
                {
                    FilterPredicate = "FROM c WHERE c.status = 'Pending'"
                },
                cancellationToken);

            return (true, response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return (false, null);
        }
    }

    public async Task<List<ProcessStepRecord>> GetProcessStepsForProcess(
        Guid processId,
        CancellationToken cancellationToken)
    {
        var processIdString = processId.ToString();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.processId = @processId ORDER BY c.createdAt ASC")
            .WithParameter("@processId", processIdString);

        return await QueryToListAsync(
            query,
            cancellationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(processIdString)
            });
    }

    public async Task CompleteProcessStepAsync(
        Guid taskInstanceId,
        Guid processId,
        Dictionary<string, JsonElement>? effectiveInputData,
        Dictionary<string, JsonElement> outputData,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var operations = new List<PatchOperation>
        {
            PatchOperation.Set("/status", ProcessStepStatus.Completed.ToString()),
            PatchOperation.Set("/updatedAt", completedAt),
            PatchOperation.Set("/completedAt", completedAt),
            PatchOperation.Set<object?>("/failedAt", null),
            PatchOperation.Set<object?>("/errorMessage", null),
            PatchOperation.Set("/outputData", outputData)
        };

        if (effectiveInputData is not null)
        {
            operations.Add(PatchOperation.Set("/inputData", effectiveInputData));
        }

        await Container.PatchItemAsync<ProcessStepRecord>(
            taskInstanceId.ToString(),
            new PartitionKey(processId.ToString()),
            operations,
            cancellationToken: cancellationToken);
    }

    public async Task FailProcessStepAsync(
        Guid taskInstanceId,
        Guid processId,
        Dictionary<string, JsonElement>? effectiveInputData,
        string? errorMessage,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        var operations = new List<PatchOperation>
        {
            PatchOperation.Set("/status", ProcessStepStatus.Failed.ToString()),
            PatchOperation.Set("/updatedAt", failedAt),
            PatchOperation.Set("/failedAt", failedAt),
            PatchOperation.Set("/errorMessage", errorMessage)
        };

        if (effectiveInputData is not null)
        {
            operations.Add(PatchOperation.Set("/inputData", effectiveInputData));
        }

        await Container.PatchItemAsync<ProcessStepRecord>(
            taskInstanceId.ToString(),
            new PartitionKey(processId.ToString()),
            operations,
            cancellationToken: cancellationToken);
    }

    public async Task<ProcessStepRecord> UpdateProcessStep(
        ProcessStepRecord state,
        CancellationToken cancellationToken)
    {
        return (await ReplaceAsync(state, cancellationToken, state.ETag)).Resource;
    }

    public async Task<bool> TryUpdateProcessStep(
        ProcessStepRecord state,
        CancellationToken cancellationToken)
    {
        try
        {
            await UpdateProcessStep(state, cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed || ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }
}

using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;
using System.Net;

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

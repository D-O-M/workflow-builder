using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

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
        return (await ReplaceAsync(state, cancellationToken)).Resource;
    }
}

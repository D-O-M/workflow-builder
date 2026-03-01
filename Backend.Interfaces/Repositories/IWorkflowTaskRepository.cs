using Backend.Models;
using Backend.Models.DomainModels;

namespace Backend.Interfaces.Repositories;

public interface IWorkflowTaskRepository
{
    Task<List<WorkflowTaskInfo>> GetAll(CancellationToken cancellationToken);

    Task CreateTaskSchema(Manifest manifest, CancellationToken cancellationToken);

    Task CreateBasicTask(string name, string version,
        string entryPath, CancellationToken cancellationToken);

    Task CreateVersion(TaskVersionDTO version, CancellationToken cancellationToken);
}

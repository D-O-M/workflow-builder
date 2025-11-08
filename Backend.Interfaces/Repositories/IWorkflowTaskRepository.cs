using Backend.DTOs;

namespace Backend.Interfaces.Repositories;

public interface IWorkflowTaskRepository
{
    Task<List<WorkflowTaskInfo>> GetAll(CancellationToken cancellationToken);

    Task CreateWorkflowTask(Manifest manifest, CancellationToken cancellationToken);
}

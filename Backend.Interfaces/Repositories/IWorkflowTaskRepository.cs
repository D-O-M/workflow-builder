using Backend.DTOs;

namespace Backend.Interfaces.Repositories;

public interface IWorkflowTaskRepository
{
    Task<List<WorkflowTaskInfo>> GetAll(CancellationToken cancellationToken);

    Task CreateTaskSchema(Manifest manifest, CancellationToken cancellationToken);
}

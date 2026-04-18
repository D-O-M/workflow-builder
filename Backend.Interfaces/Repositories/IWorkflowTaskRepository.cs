using Backend.Models;
using Backend.Models.DomainModels;
using Backend.Models.ViewModels;

namespace Backend.Interfaces.Repositories;

public interface IWorkflowTaskRepository
{
    Task<List<WorkflowTaskInfo>> GetAll(CancellationToken cancellationToken);

    Task<List<TaskDefinitionViewModel>> GetAllTaskDefinitionViewModels(CancellationToken cancellationToken);

    Task CreateTaskSchema(Manifest manifest, CancellationToken cancellationToken);

    Task CreateBasicTask(string name, string version,
        string entryPath, CancellationToken cancellationToken);

    Task<TaskVersionRegistrationResult> CreateVersion(TaskVersionDomainModel version, CancellationToken cancellationToken);

    Task PromoteVersionAsync(int taskVersionId, CancellationToken cancellationToken);

    Task MarkVersionSupersededAsync(int taskVersionId, CancellationToken cancellationToken);

    Task MarkVersionFailedAsync(int taskVersionId, CancellationToken cancellationToken);
}

using Backend.DTOs;

namespace Backend.Interfaces.Repositories;

public interface IWorkflowBlueprintRepository
{
    Task<WorkflowBlueprint> GetFullBlueprintById(
        int idd, CancellationToken cancellationToken);
}

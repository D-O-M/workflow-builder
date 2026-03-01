using Backend.Models;

namespace Backend.Interfaces.Repositories;

public interface IWorkflowBlueprintRepository
{
    Task<WorkflowBlueprint> GetFullBlueprintById(
        int idd, CancellationToken cancellationToken);
}

using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IWorkflowInstanceRepository<TGlobalState>
{
    Task<WorkflowObject<TGlobalState>> CreateWorkflowState(
        WorkflowObject<TGlobalState> state,
        CancellationToken cancellationToken);

    Task<WorkflowObject<TGlobalState>> GetWorkflowStateById(
        Guid workflowInstanceId,
        int workflowBlueprintId,
        CancellationToken cancellationToken);

    Task<List<WorkflowObject<TGlobalState>>> GetWorkflowStatesForBlueprint(
        int workflowBlueprintId,
        CancellationToken cancellationToken);

    Task<WorkflowObject<TGlobalState>> UpdateWorkflowState(
        WorkflowObject<TGlobalState> state,
        CancellationToken cancellationToken);
}

using Backend.State.DAL;

namespace Backend.Interfaces.Repositories;
public interface IWorkflowStateRepository
{
    Task<WorkflowInstanceState> GetWorkflowStateById(Guid workflowInstanceId);

    Task<WorkflowInstanceState> UpdateWorkflowState(WorkflowInstanceState state);
}

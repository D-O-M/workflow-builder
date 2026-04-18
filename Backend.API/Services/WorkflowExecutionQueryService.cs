using Backend.Repositories.Execution;
using Backend.Models.ViewModels;

namespace Backend.API.Services;

public sealed class WorkflowExecutionQueryService
{
    private readonly WorkflowExecutionProjectionService _projectionService;

    public WorkflowExecutionQueryService(WorkflowExecutionProjectionService projectionService)
    {
        _projectionService = projectionService;
    }

    public async Task<List<WorkflowExecutionSummaryViewModel>> GetExecutionsForBlueprintDefinitionAsync(
        int blueprintDefinitionId,
        CancellationToken cancellationToken) =>
        await _projectionService.GetExecutionsForBlueprintDefinitionAsync(
            blueprintDefinitionId,
            cancellationToken);

    public async Task<WorkflowExecutionDetailViewModel> GetExecutionDetailAsync(
        int blueprintDefinitionId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken) =>
        await _projectionService.GetExecutionDetailAsync(
            blueprintDefinitionId,
            workflowInstanceId,
            cancellationToken);
}

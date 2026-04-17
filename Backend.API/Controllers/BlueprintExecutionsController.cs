using Backend.API.Services;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[Route("api/blueprints/{blueprintDefinitionId:int}/executions")]
[ApiController]
public sealed class BlueprintExecutionsController : ControllerBase
{
    private readonly WorkflowExecutionQueryService _workflowExecutionQueryService;

    public BlueprintExecutionsController(WorkflowExecutionQueryService workflowExecutionQueryService)
    {
        _workflowExecutionQueryService = workflowExecutionQueryService;
    }

    [HttpGet]
    public async Task<ActionResult<List<WorkflowExecutionSummaryViewModel>>> GetExecutions(
        [FromRoute] int blueprintDefinitionId)
    {
        try
        {
            var executions = await _workflowExecutionQueryService.GetExecutionsForBlueprintDefinitionAsync(
                blueprintDefinitionId,
                HttpContext.RequestAborted);
            return Ok(executions);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
    }

    [HttpGet("{workflowInstanceId:guid}")]
    public async Task<ActionResult<WorkflowExecutionDetailViewModel>> GetExecution(
        [FromRoute] int blueprintDefinitionId,
        [FromRoute] Guid workflowInstanceId)
    {
        try
        {
            var execution = await _workflowExecutionQueryService.GetExecutionDetailAsync(
                blueprintDefinitionId,
                workflowInstanceId,
                HttpContext.RequestAborted);
            return Ok(execution);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
        catch (EntityNotFoundException<WorkflowExecutionDetailViewModel>)
        {
            return NotFound();
        }
    }
}

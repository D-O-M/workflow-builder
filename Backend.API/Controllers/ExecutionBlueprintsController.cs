using Backend.API.Services;
using Backend.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[Route("api/executions/blueprints")]
[ApiController]
public sealed class ExecutionBlueprintsController : ControllerBase
{
    private readonly WorkflowExecutionQueryService _workflowExecutionQueryService;

    public ExecutionBlueprintsController(WorkflowExecutionQueryService workflowExecutionQueryService)
    {
        _workflowExecutionQueryService = workflowExecutionQueryService;
    }

    [HttpGet]
    public async Task<ActionResult<BlueprintExecutionActivityPageViewModel>> GetBlueprints(
        [FromQuery] string? query = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        var activities = await _workflowExecutionQueryService.GetExecutionBlueprintActivitiesAsync(
            query,
            page,
            pageSize,
            HttpContext.RequestAborted);

        return Ok(activities);
    }
}

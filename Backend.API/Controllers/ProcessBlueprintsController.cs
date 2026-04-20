using Backend.API.Services;
using Backend.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[Route("api/processes/blueprints")]
[ApiController]
public sealed class ProcessBlueprintsController : ControllerBase
{
    private readonly ProcessQueryService _processQueryService;

    public ProcessBlueprintsController(ProcessQueryService processQueryService)
    {
        _processQueryService = processQueryService;
    }

    [HttpGet]
    public async Task<ActionResult<BlueprintProcessActivityPageViewModel>> GetBlueprints(
        [FromQuery] string? query = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        var activities = await _processQueryService.GetProcessBlueprintActivitiesAsync(
            query,
            page,
            pageSize,
            HttpContext.RequestAborted);

        return Ok(activities);
    }
}

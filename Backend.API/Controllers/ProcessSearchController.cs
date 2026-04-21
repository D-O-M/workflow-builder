using Backend.API.Services;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[Route("api/processes/search")]
[ApiController]
public sealed class ProcessSearchController : ControllerBase
{
    private readonly ProcessQueryService _processQueryService;

    public ProcessSearchController(ProcessQueryService processQueryService)
    {
        _processQueryService = processQueryService;
    }

    [HttpPost]
    public async Task<ActionResult<ProcessSearchPageViewModel>> SearchProcesses(
        [FromBody] ProcessSearchRequestInputModel request)
    {
        try
        {
            return Ok(await _processQueryService.SearchProcessesAsync(request, HttpContext.RequestAborted));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new
            {
                error = exception.Message,
            });
        }
    }
}

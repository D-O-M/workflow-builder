using Backend.API.Services;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[Route("api/processes")]
[ApiController]
public sealed class ProcessesController : ControllerBase
{
    private readonly ProcessQueryService _processQueryService;

    public ProcessesController(ProcessQueryService processQueryService)
    {
        _processQueryService = processQueryService;
    }

    [HttpGet("{processId:guid}")]
    public async Task<ActionResult<ProcessDetailViewModel>> GetProcess(
        [FromRoute] Guid processId)
    {
        try
        {
            return Ok(await _processQueryService.GetProcessDetailByIdAsync(
                processId,
                HttpContext.RequestAborted));
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
        catch (EntityNotFoundException<BlueprintVersionViewModel>)
        {
            return NotFound();
        }
        catch (EntityNotFoundException<ProcessDetailViewModel>)
        {
            return NotFound();
        }
    }
}

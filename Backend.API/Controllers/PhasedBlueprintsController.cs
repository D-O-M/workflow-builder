using Backend.API.Services;
using Backend.Interfaces.Repositories;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/phased-blueprints")]
public sealed class PhasedBlueprintsController : ControllerBase
{
    private readonly IPhasedBlueprintRepository _phasedBlueprintRepository;
    private readonly PhasedBlueprintProcessService _phasedBlueprintProcessService;

    public PhasedBlueprintsController(
        IPhasedBlueprintRepository phasedBlueprintRepository,
        PhasedBlueprintProcessService phasedBlueprintProcessService)
    {
        _phasedBlueprintRepository = phasedBlueprintRepository;
        _phasedBlueprintProcessService = phasedBlueprintProcessService;
    }

    [HttpGet]
    public async Task<ActionResult<List<PhasedBlueprintViewModel>>> GetAll()
    {
        var items = await _phasedBlueprintRepository.GetAllAsync(HttpContext.RequestAborted);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PhasedBlueprintViewModel>> GetById([FromRoute] int id)
    {
        try
        {
            return Ok(await _phasedBlueprintRepository.GetByIdAsync(id, HttpContext.RequestAborted));
        }
        catch (EntityNotFoundException<PhasedBlueprintViewModel>)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<ActionResult<PhasedBlueprintViewModel>> Create(
        [FromBody] PhasedBlueprintInputModel model)
    {
        try
        {
            var created = await _phasedBlueprintRepository.CreateAsync(model, HttpContext.RequestAborted);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (InvalidDataException exception)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Phased blueprint validation failed.",
                Detail = exception.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    [HttpPost("{id:int}/versions")]
    public async Task<ActionResult<PhasedBlueprintVersionViewModel>> CreateVersion(
        [FromRoute] int id,
        [FromBody] PhasedBlueprintVersionInputModel model)
    {
        if (model.PhasedBlueprintDefinitionId != id)
        {
            return BadRequest();
        }

        try
        {
            return Ok(await _phasedBlueprintRepository.CreateVersionAsync(model, HttpContext.RequestAborted));
        }
        catch (EntityNotFoundException<PhasedBlueprintViewModel>)
        {
            return NotFound();
        }
        catch (InvalidDataException exception)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Phased blueprint version validation failed.",
                Detail = exception.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    [HttpPost("{id:int}/processes")]
    public async Task<ActionResult<PhasedProcessAcceptedViewModel>> StartProcess(
        [FromRoute] int id,
        [FromBody] PhasedBlueprintProcessInputModel model)
    {
        try
        {
            return Accepted(await _phasedBlueprintProcessService.StartAsync(
                id,
                model,
                HttpContext.RequestAborted));
        }
        catch (EntityNotFoundException<PhasedBlueprintViewModel>)
        {
            return NotFound();
        }
        catch (InvalidDataException exception)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Phased process start failed.",
                Detail = exception.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    [HttpPost("{id:int}/phases/{phaseIndex:int}/triggers")]
    public async Task<ActionResult<PhasedProcessAcceptedViewModel>> TriggerPhase(
        [FromRoute] int id,
        [FromRoute] int phaseIndex,
        [FromBody] PhasedBlueprintTriggerInputModel model)
    {
        try
        {
            return Accepted(await _phasedBlueprintProcessService.TriggerPhaseAsync(
                id,
                phaseIndex,
                model,
                HttpContext.RequestAborted));
        }
        catch (EntityNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidDataException exception)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Phase trigger failed.",
                Detail = exception.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    [HttpGet("{id:int}/processes/{processId:guid}")]
    public async Task<ActionResult<PhasedProcessDetailViewModel>> GetProcess(
        [FromRoute] int id,
        [FromRoute] Guid processId)
    {
        try
        {
            return Ok(await _phasedBlueprintProcessService.GetDetailAsync(
                id,
                processId,
                HttpContext.RequestAborted));
        }
        catch (EntityNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{id:int}/processes/{processId:guid}/overview")]
    public async Task<ActionResult<PhasedProcessOverviewViewModel>> GetProcessOverview(
        int id, Guid processId, [FromServices] PhasedProcessOverviewService overview)
    {
        try
        {
            return Ok(await overview.GetOverviewAsync(id, processId, HttpContext.RequestAborted));
        }
        catch (EntityNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{id:int}/processes/{processId:guid}/handoffs/{sourcePhaseId:guid}")]
    public async Task<ActionResult<PhasedProcessHandoffViewModel>> GetProcessHandoff(
        int id, Guid processId, Guid sourcePhaseId, [FromServices] PhasedProcessOverviewService overview)
    {
        try
        {
            return Ok(await overview.GetHandoffAsync(id, processId, sourcePhaseId, HttpContext.RequestAborted));
        }
        catch (EntityNotFoundException)
        {
            return NotFound();
        }
    }
}

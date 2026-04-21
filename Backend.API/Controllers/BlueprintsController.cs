using Backend.API.Services;
using Backend.Models;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Backend.API.Validation.Blueprints;
using Backend.Interfaces.Repositories;
using Backend.Repositories;

namespace Backend.API.Controllers;

[Route("api/blueprints")]
[ApiController]
public class BlueprintsController : ControllerBase
{
    private readonly IBlueprintVersionValidator _blueprintVersionValidator;
    private readonly BlueprintProcessService _blueprintProcessService;
    private readonly BlueprintPortabilityService _blueprintPortabilityService;
    private readonly IBlueprintRepository _blueprintRepository;

    public BlueprintsController(
        IBlueprintVersionValidator blueprintVersionValidator,
        BlueprintProcessService blueprintProcessService,
        BlueprintPortabilityService blueprintPortabilityService,
        IBlueprintRepository blueprintRepository)
    {
        _blueprintVersionValidator = blueprintVersionValidator;
        _blueprintProcessService = blueprintProcessService;
        _blueprintPortabilityService = blueprintPortabilityService;
        _blueprintRepository = blueprintRepository;
    }

    [HttpGet]
    public async Task<ActionResult<List<BlueprintViewModel>>> GetAllBlueprints()
    {
        var items = await _blueprintRepository.GetAllBlueprints(HttpContext.RequestAborted);
        return Ok(items);
    }

    [HttpGet("{Id:int}")]
    public async Task<ActionResult<BlueprintViewModel>> GetBlueprintById(
        [FromRoute] IdInputModel model)
    {
        try
        {
            var blueprint = await _blueprintRepository.GetBlueprintById(model.Id, HttpContext.RequestAborted);
            return Ok(blueprint);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<ActionResult<BlueprintViewModel>> CreateBlueprint(
        [FromBody] BlueprintInputModel model)
    {
        if (model.Tasks.Count == 0)
            return BadRequest();

        var validation = await _blueprintVersionValidator.ValidateAsync(
            new BlueprintVersionInputModel
            {
                BlueprintDefinitionId = 1,
                BaseVersion = null,
                Description = model.Description,
                StartingBlueprintNodeId = model.StartingBlueprintNodeId,
                Tasks = model.Tasks
            },
            HttpContext.RequestAborted);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Blueprint validation failed.",
                Status = StatusCodes.Status400BadRequest,
                Extensions = { ["errors"] = validation.Errors }
            });

        try
        {
            var created = await _blueprintRepository.CreateBlueprint(model, HttpContext.RequestAborted);
            return CreatedAtAction(nameof(GetBlueprintById), new { Id = created.Id }, created);
        }
        catch (InvalidDataException)
        {
            return BadRequest();
        }
    }

    [HttpPost, Route("{Id:int}/versions")]
    public async Task<ActionResult<BlueprintVersionViewModel>> CreateVersion(
        [FromRoute] IdInputModel idModel, [FromBody] BlueprintVersionInputModel model)
    {
        if (model.BlueprintDefinitionId != idModel.Id)
            return BadRequest();

        if (model.Tasks.Count == 0)
            return BadRequest();

        var validation = await _blueprintVersionValidator.ValidateAsync(model, HttpContext.RequestAborted);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Blueprint version validation failed.",
                Status = StatusCodes.Status400BadRequest,
                Extensions = { ["errors"] = validation.Errors }
            });

        try
        {
            var created = await _blueprintRepository.CreateVersion(model, HttpContext.RequestAborted);
            return Ok(created);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
    }

    [HttpPost("{Id:int}/processes")]
    public async Task<ActionResult<object>> StartProcess(
        [FromRoute] IdInputModel model,
        [FromBody] BlueprintProcessInputModel processModel)
    {
        try
        {
            var accepted = await _blueprintProcessService.StartLatestVersionAsync(
                model.Id,
                processModel,
                HttpContext.RequestAborted);

            return Accepted(accepted);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
        catch (InvalidDataException)
        {
            return BadRequest();
        }
    }

    [HttpGet("{Id:int}/export")]
    public async Task<ActionResult<ExportedBlueprintViewModel>> ExportBlueprint(
        [FromRoute] IdInputModel model)
    {
        try
        {
            var exported = await _blueprintPortabilityService.ExportLatestBlueprintAsync(
                model.Id,
                HttpContext.RequestAborted);
            return Ok(exported);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
    }

    [HttpPost("import")]
    public async Task<ActionResult<BlueprintImportViewModel>> ImportBlueprint(
        [FromBody] ExportedBlueprintInputModel model)
    {
        try
        {
            var imported = await _blueprintPortabilityService.ImportBlueprintAsync(
                model,
                HttpContext.RequestAborted);
            if (imported.Status == BlueprintImportStatus.DisplayNameConflict)
            {
                return Conflict(imported);
            }
            return Ok(imported);
        }
        catch (BlueprintImportValidationException ex)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Blueprint import validation failed.",
                Status = StatusCodes.Status400BadRequest,
                Extensions = { ["errors"] = ex.Errors }
            });
        }
    }
}

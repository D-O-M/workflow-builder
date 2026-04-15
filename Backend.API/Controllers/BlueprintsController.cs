using Backend.Models.DomainModels;
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
    private readonly IWorkflowBlueprintRepository _workflowBlueprintRepository;

    public BlueprintsController(
        IBlueprintVersionValidator blueprintVersionValidator,
        IWorkflowBlueprintRepository workflowBlueprintRepository)
    {
        _blueprintVersionValidator = blueprintVersionValidator;
        _workflowBlueprintRepository = workflowBlueprintRepository;
    }

    [HttpGet]
    public async Task<ActionResult<List<BlueprintViewModel>>> GetAllBlueprints()
    {
        var items = await _workflowBlueprintRepository.GetAllBlueprints(HttpContext.RequestAborted);
        return Ok(items);
    }

    [HttpGet("{Id:int}")]
    public async Task<ActionResult<BlueprintViewModel>> GetBlueprintById(
        [FromRoute] IdInputModel model)
    {
        try
        {
            var blueprint = await _workflowBlueprintRepository.GetBlueprintById(model.Id, HttpContext.RequestAborted);
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

        try
        {
            var created = await _workflowBlueprintRepository.CreateBlueprint(model, HttpContext.RequestAborted);
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

        var taskModelsByInstanceId = model.Tasks.ToDictionary(t => t.InstanceId, t => t);

        var dto = new FullBlueprintVersion
        {
            Name = model.Name,
            DefinitionId = model.BlueprintDefinitionId,
            Description = model.Description,
            TaskTree = BuildTaskTree(
                taskModelsByInstanceId[model.StartingTaskInstanceId],
                taskModelsByInstanceId)
        };

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
            var created = await _workflowBlueprintRepository.CreateVersion(dto, HttpContext.RequestAborted);
            return Ok(created);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
    }

    [HttpPost("{Id:int}/execute")]
    public async Task<ActionResult<object>> ExecuteBlueprint(
        [FromRoute] IdInputModel model,
        [FromBody] BlueprintExecutionInputModel executionModel)
    {
        try
        {
            await _workflowBlueprintRepository.GetBlueprintById(model.Id, HttpContext.RequestAborted);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }

        return Accepted(new
        {
            WorkflowInstanceId = Guid.NewGuid(),
            BlueprintId = model.Id,
            AcceptedAt = DateTimeOffset.UtcNow,
            GlobalState = executionModel.GlobalState,
            InitialTaskInput = executionModel.InitialTaskInput,
        });
    }

    private BlueprintTaskTree BuildTaskTree(
        BlueprintTaskInputModel model,
        Dictionary<Guid, BlueprintTaskInputModel> taskModelsByInstanceId)
    {
        var task = new BlueprintTaskTree
        {
            InstanceId = model.InstanceId,
            VersionId = model.VersionId,
            Position = new(model.Position.X, model.Position.Y)
        };

        if (model.OutgoingConnections.Count > 0)
        {
            foreach (var connection in model.OutgoingConnections)
            {
                task.OutgoingConnections.Add(
                    new() {
                        ChannelDetails = new()
                        {
                            ChannelId = connection.ChannelId,
                            ChannelName = connection.ChannelName
                        },
                        Anchor = connection.Anchor,
                        Side = connection.Side,
                        Position = connection.Position,
                        Task = BuildTaskTree(
                            taskModelsByInstanceId[connection.ConnectedTaskInstanceId],
                            taskModelsByInstanceId)
                    });
            }
        }

        return task;
    }
}

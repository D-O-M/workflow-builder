using Backend.Models.DomainModels;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Backend.API.Validation.Blueprints;

namespace Backend.API.Controllers;

[Route("api/blueprints")]
[ApiController]
public class BlueprintsController : ControllerBase
{
    private readonly IBlueprintVersionValidator _blueprintVersionValidator;

    public BlueprintsController(IBlueprintVersionValidator blueprintVersionValidator)
        => _blueprintVersionValidator = blueprintVersionValidator;

    [HttpGet]
    public async Task<ActionResult<List<BlueprintViewModel>>> GetAllBlueprints()
    {
        throw new NotImplementedException();
    }

    [HttpGet("{Id:int}")]
    public async Task<ActionResult<BlueprintViewModel>> GetBlueprintById(
        [FromRoute] IdInputModel model)
    {
        throw new NotImplementedException();
    }

    [HttpPost]
    public async Task<ActionResult<BlueprintViewModel>> CreateBlueprint(
        [FromBody] BlueprintInputModel model)
    {
        throw new NotImplementedException();
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

        throw new NotImplementedException();
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
                        Task = BuildTaskTree(
                            taskModelsByInstanceId[connection.ConnectedTaskInstanceId],
                            taskModelsByInstanceId)
                    });
            }
        }

        return task;
    }
}

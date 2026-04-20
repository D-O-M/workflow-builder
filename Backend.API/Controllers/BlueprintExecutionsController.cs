using System.Text.Json;
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

    [HttpGet("{workflowInstanceId:guid}/steps/{blueprintTaskInstanceId:guid}")]
    public async Task<ActionResult<WorkflowExecutionStepDetailViewModel>> GetExecutionStep(
        [FromRoute] int blueprintDefinitionId,
        [FromRoute] Guid workflowInstanceId,
        [FromRoute] Guid blueprintTaskInstanceId)
    {
        try
        {
            var step = await _workflowExecutionQueryService.GetExecutionStepDetailAsync(
                blueprintDefinitionId,
                workflowInstanceId,
                blueprintTaskInstanceId,
                HttpContext.RequestAborted);
            return Ok(step);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
        catch (EntityNotFoundException<WorkflowExecutionDetailViewModel>)
        {
            return NotFound();
        }
        catch (EntityNotFoundException<WorkflowExecutionStepDetailViewModel>)
        {
            return NotFound();
        }
    }

    [HttpGet("{workflowInstanceId:guid}/steps/{blueprintTaskInstanceId:guid}/logs")]
    public async Task<ActionResult<NodeLogBatchViewModel>> GetExecutionStepLogs(
        [FromRoute] int blueprintDefinitionId,
        [FromRoute] Guid workflowInstanceId,
        [FromRoute] Guid blueprintTaskInstanceId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int limit = 250,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _workflowExecutionQueryService.GetExecutionStepLogsAsync(
                blueprintDefinitionId,
                workflowInstanceId,
                blueprintTaskInstanceId,
                from,
                to,
                limit,
                cursor,
                cancellationToken));
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
        catch (EntityNotFoundException<WorkflowExecutionDetailViewModel>)
        {
            return NotFound();
        }
        catch (EntityNotFoundException<WorkflowExecutionStepDetailViewModel>)
        {
            return NotFound();
        }
    }

    [HttpGet("{workflowInstanceId:guid}/steps/{blueprintTaskInstanceId:guid}/logs/stream")]
    public async Task StreamExecutionStepLogs(
        [FromRoute] int blueprintDefinitionId,
        [FromRoute] Guid workflowInstanceId,
        [FromRoute] Guid blueprintTaskInstanceId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var currentCursor = cursor;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var batch = await _workflowExecutionQueryService.GetExecutionStepLogsAsync(
                    blueprintDefinitionId,
                    workflowInstanceId,
                    blueprintTaskInstanceId,
                    from,
                    to,
                    250,
                    currentCursor,
                    cancellationToken);

                if (batch.Entries.Count > 0)
                {
                    currentCursor = batch.NextCursor;
                    await WriteServerSentEventAsync("logBatch", batch, cancellationToken);
                }
                else
                {
                    await Response.WriteAsync(": keepalive\n\n", cancellationToken);
                }

                await Response.Body.FlushAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (EntityNotFoundException<BlueprintViewModel>)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            catch (EntityNotFoundException<WorkflowExecutionDetailViewModel>)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            catch (EntityNotFoundException<WorkflowExecutionStepDetailViewModel>)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
    }

    [HttpGet("/api/blueprints/{blueprintDefinitionId:int}/execution-history")]
    public async Task<ActionResult<BlueprintExecutionHistoryPageViewModel>> GetExecutionHistory(
        [FromRoute] int blueprintDefinitionId,
        [FromQuery] int limit = 25,
        [FromQuery] string? cursor = null)
    {
        try
        {
            var historyPage = await _workflowExecutionQueryService.GetExecutionHistoryAsync(
                blueprintDefinitionId,
                limit,
                cursor,
                HttpContext.RequestAborted);
            return Ok(historyPage);
        }
        catch (ArgumentException exception) when (string.Equals(exception.ParamName, "cursor", StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                error = exception.Message,
            });
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
    }

    private async Task WriteServerSentEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        var serializedPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await Response.WriteAsync($"data: {serializedPayload}\n\n", cancellationToken);
    }
}

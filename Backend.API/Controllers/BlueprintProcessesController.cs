using System.Text.Json;
using Backend.API.Services;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[Route("api/blueprints/{blueprintDefinitionId:int}/processes")]
[ApiController]
public sealed class BlueprintProcessesController : ControllerBase
{
    private readonly ProcessQueryService _processQueryService;

    public BlueprintProcessesController(ProcessQueryService processQueryService)
    {
        _processQueryService = processQueryService;
    }

    [HttpGet]
    public async Task<ActionResult<List<ProcessSummaryViewModel>>> GetProcesses(
        [FromRoute] int blueprintDefinitionId)
    {
        try
        {
            var processes = await _processQueryService.GetProcessesForBlueprintDefinitionAsync(
                blueprintDefinitionId,
                HttpContext.RequestAborted);
            return Ok(processes);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
    }

    [HttpGet("{processId:guid}")]
    public async Task<ActionResult<ProcessDetailViewModel>> GetProcess(
        [FromRoute] int blueprintDefinitionId,
        [FromRoute] Guid processId)
    {
        try
        {
            var process = await _processQueryService.GetProcessDetailAsync(
                blueprintDefinitionId,
                processId,
                HttpContext.RequestAborted);
            return Ok(process);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
        catch (EntityNotFoundException<ProcessDetailViewModel>)
        {
            return NotFound();
        }
    }

    [HttpGet("{processId:guid}/steps/{blueprintNodeId:guid}")]
    public async Task<ActionResult<ProcessStepDetailViewModel>> GetProcessStep(
        [FromRoute] int blueprintDefinitionId,
        [FromRoute] Guid processId,
        [FromRoute] Guid blueprintNodeId)
    {
        try
        {
            var step = await _processQueryService.GetProcessStepDetailAsync(
                blueprintDefinitionId,
                processId,
                blueprintNodeId,
                HttpContext.RequestAborted);
            return Ok(step);
        }
        catch (EntityNotFoundException<BlueprintViewModel>)
        {
            return NotFound();
        }
        catch (EntityNotFoundException<ProcessDetailViewModel>)
        {
            return NotFound();
        }
        catch (EntityNotFoundException<ProcessStepDetailViewModel>)
        {
            return NotFound();
        }
    }

    [HttpGet("{processId:guid}/steps/{blueprintNodeId:guid}/logs")]
    public async Task<ActionResult<ProcessStepLogBatchViewModel>> GetProcessStepLogs(
        [FromRoute] int blueprintDefinitionId,
        [FromRoute] Guid processId,
        [FromRoute] Guid blueprintNodeId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int limit = 250,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _processQueryService.GetProcessStepLogsAsync(
                blueprintDefinitionId,
                processId,
                blueprintNodeId,
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
        catch (EntityNotFoundException<ProcessDetailViewModel>)
        {
            return NotFound();
        }
        catch (EntityNotFoundException<ProcessStepDetailViewModel>)
        {
            return NotFound();
        }
    }

    [HttpGet("{processId:guid}/steps/{blueprintNodeId:guid}/logs/stream")]
    public async Task StreamProcessStepLogs(
        [FromRoute] int blueprintDefinitionId,
        [FromRoute] Guid processId,
        [FromRoute] Guid blueprintNodeId,
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
                var batch = await _processQueryService.GetProcessStepLogsAsync(
                    blueprintDefinitionId,
                    processId,
                    blueprintNodeId,
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
            catch (EntityNotFoundException<ProcessDetailViewModel>)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            catch (EntityNotFoundException<ProcessStepDetailViewModel>)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
    }

    [HttpGet("history")]
    public async Task<ActionResult<BlueprintProcessHistoryPageViewModel>> GetProcessHistory(
        [FromRoute] int blueprintDefinitionId,
        [FromQuery] int limit = 25,
        [FromQuery] string? cursor = null)
    {
        try
        {
            var historyPage = await _processQueryService.GetProcessHistoryAsync(
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

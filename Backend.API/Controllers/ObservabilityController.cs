using System.Text.Json;
using Backend.API.Observability;
using Backend.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/observability")]
public sealed class ObservabilityController : ControllerBase
{
    private readonly ObservabilityQueryService _observabilityQueryService;

    public ObservabilityController(ObservabilityQueryService observabilityQueryService)
    {
        _observabilityQueryService = observabilityQueryService;
    }

    [HttpGet("processes/summary")]
    public async Task<ActionResult<ProcessObservabilitySummaryViewModel>> GetProcessSummary(
        [FromQuery] int? blueprintDefinitionId,
        [FromQuery] string? window,
        CancellationToken cancellationToken)
    {
        return Ok(await _observabilityQueryService.GetProcessSummaryAsync(
            blueprintDefinitionId,
            window,
            cancellationToken));
    }

    [HttpGet("nodes/summary")]
    public async Task<ActionResult<NodeObservabilitySummaryViewModel>> GetNodeSummary(
        [FromQuery] int blueprintDefinitionId,
        [FromQuery] string? window,
        CancellationToken cancellationToken)
    {
        return Ok(await _observabilityQueryService.GetNodeSummaryAsync(
            blueprintDefinitionId,
            window,
            cancellationToken));
    }

    [HttpGet("processes/recent")]
    public async Task<ActionResult<RecentProcessesViewModel>> GetRecentProcesses(
        [FromQuery] int? blueprintDefinitionId,
        [FromQuery] string? window,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _observabilityQueryService.GetRecentProcessesAsync(
            blueprintDefinitionId,
            window,
            limit,
            cancellationToken));
    }

    [HttpGet("processes/{processId:guid}/logs")]
    public async Task<ActionResult<ProcessLogBatchViewModel>> GetProcessLogs(
        Guid processId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int limit = 250,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _observabilityQueryService.GetProcessLogsAsync(
            processId,
            from,
            to,
            limit,
            cursor,
            cancellationToken));
    }

    [HttpGet("processes/{processId:guid}/logs/stream")]
    public async Task StreamProcessLogs(
        Guid processId,
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
            var batch = await _observabilityQueryService.GetProcessLogsAsync(
                processId,
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
    }

    [HttpGet("processes/{processId:guid}/steps/{runtimeProcessStepId:guid}/logs")]
    public async Task<ActionResult<ProcessStepLogBatchViewModel>> GetProcessStepLogs(
        Guid processId,
        Guid runtimeProcessStepId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int limit = 250,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _observabilityQueryService.GetProcessStepLogsByRuntimeIdAsync(
            processId,
            runtimeProcessStepId,
            from,
            to,
            limit,
            cursor,
            cancellationToken));
    }

    [HttpGet("processes/{processId:guid}/steps/{runtimeProcessStepId:guid}/logs/stream")]
    public async Task StreamProcessStepLogs(
        Guid processId,
        Guid runtimeProcessStepId,
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
            var batch = await _observabilityQueryService.GetProcessStepLogsByRuntimeIdAsync(
                processId,
                runtimeProcessStepId,
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
    }

    private async Task WriteServerSentEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        var serializedPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await Response.WriteAsync($"data: {serializedPayload}\n\n", cancellationToken);
    }
}

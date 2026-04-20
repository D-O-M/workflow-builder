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

    [HttpGet("workflows/summary")]
    public async Task<ActionResult<WorkflowObservabilitySummaryViewModel>> GetWorkflowSummary(
        [FromQuery] int? blueprintDefinitionId,
        [FromQuery] string? window,
        CancellationToken cancellationToken)
    {
        return Ok(await _observabilityQueryService.GetWorkflowSummaryAsync(
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

    [HttpGet("workflows/recent")]
    public async Task<ActionResult<WorkflowRecentRunsViewModel>> GetRecentRuns(
        [FromQuery] int? blueprintDefinitionId,
        [FromQuery] string? window,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _observabilityQueryService.GetRecentRunsAsync(
            blueprintDefinitionId,
            window,
            limit,
            cancellationToken));
    }

    [HttpGet("workflows/{workflowInstanceId:guid}/logs")]
    public async Task<ActionResult<WorkflowLogBatchViewModel>> GetWorkflowLogs(
        Guid workflowInstanceId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int limit = 250,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _observabilityQueryService.GetWorkflowLogsAsync(
            workflowInstanceId,
            from,
            to,
            limit,
            cursor,
            cancellationToken));
    }

    [HttpGet("workflows/{workflowInstanceId:guid}/logs/stream")]
    public async Task StreamWorkflowLogs(
        Guid workflowInstanceId,
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
            var batch = await _observabilityQueryService.GetWorkflowLogsAsync(
                workflowInstanceId,
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

    [HttpGet("workflows/{workflowInstanceId:guid}/nodes/{runtimeTaskInstanceId:guid}/logs")]
    public async Task<ActionResult<NodeLogBatchViewModel>> GetNodeLogs(
        Guid workflowInstanceId,
        Guid runtimeTaskInstanceId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int limit = 250,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _observabilityQueryService.GetNodeLogsAsync(
            workflowInstanceId,
            runtimeTaskInstanceId,
            from,
            to,
            limit,
            cursor,
            cancellationToken));
    }

    [HttpGet("workflows/{workflowInstanceId:guid}/nodes/{runtimeTaskInstanceId:guid}/logs/stream")]
    public async Task StreamNodeLogs(
        Guid workflowInstanceId,
        Guid runtimeTaskInstanceId,
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
            var batch = await _observabilityQueryService.GetNodeLogsAsync(
                workflowInstanceId,
                runtimeTaskInstanceId,
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

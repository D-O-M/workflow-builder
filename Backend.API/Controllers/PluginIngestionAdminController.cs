using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/admin/plugin-ingestions")]
public sealed class PluginIngestionAdminController : ControllerBase
{
    private readonly IPluginIngestionStatusStore _statusStore;

    public PluginIngestionAdminController(IPluginIngestionStatusStore statusStore)
    {
        _statusStore = statusStore;
    }

    [HttpGet]
    public async Task<ActionResult<PluginIngestionStatusPage>> Get(
        [FromQuery] string? pluginName,
        [FromQuery] PluginIngestionStatus? status,
        [FromQuery] bool? quarantined,
        [FromQuery] string? continuationToken,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var page = await _statusStore.QueryAsync(
            new PluginIngestionStatusQuery
            {
                PluginName = pluginName,
                Status = status,
                Quarantined = quarantined,
                ContinuationToken = continuationToken,
                PageSize = pageSize
            },
            cancellationToken);

        return Ok(page);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PluginIngestionStatusRecord>> GetById(
        [FromRoute] string id,
        CancellationToken cancellationToken)
    {
        var record = await _statusStore.GetAsync(id, cancellationToken);
        return record is null ? NotFound() : Ok(record);
    }

    [HttpPost("{id}/quarantine")]
    public async Task<IActionResult> Quarantine(
        [FromRoute] string id,
        [FromBody] PluginIngestionQuarantineRequest? request,
        CancellationToken cancellationToken)
    {
        await _statusStore.QuarantineAsync(id, request?.Reason, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id}/unquarantine")]
    public async Task<IActionResult> Unquarantine(
        [FromRoute] string id,
        [FromBody] PluginIngestionQuarantineRequest? request,
        CancellationToken cancellationToken)
    {
        await _statusStore.UnquarantineAsync(id, request?.Reason, cancellationToken);
        return NoContent();
    }
}

public sealed record PluginIngestionQuarantineRequest(string? Reason);

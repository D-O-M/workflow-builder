using Backend.API.Services;
using Backend.Database.Relational;
using Backend.Database.State.Objects;
using Backend.Security.Actors;
using Backend.Security.Authentication;
using Backend.Security.Authorization;
using Backend.Services.EngineManagement;
using Backend.Services.PluginRuntimeStatus;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/engine-management")]
[Authorize(Policy = WorkflowAuthorizationPolicies.RuntimeEngineer)]
[Authorize(Policy = WorkflowAuthorizationPolicies.ManagementIdentity)]
[TypeFilter<ManagementAccessFilter>]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class EngineManagementController(EngineManagementStore store, ManagementSettingsCatalog catalog,
    BlueprintDbContext database, TaskCatalogQueryService tasks, IWorkflowActorResolver actors,
    PluginRuntimeStatusOptions runtimeOptions) : ControllerBase
{
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var policy = await store.ReadAsync(ct);
        return Ok(new
        {
            policy.Revision,
            Settings = catalog.Fields.Select(field =>
            {
                var overridden = policy.Settings.TryGetValue(field.Key, out var setting);
                return new
                {
                    field.Key, field.CapabilityId, field.CapabilityName, field.Kind, field.Description,
                    field.Required, field.Secret, Overridden = overridden, Value = field.Secret ? null : setting?.Value
                };
            })
        });
    }

    [HttpGet("plugins")]
    public async Task<IActionResult> GetPlugins(CancellationToken ct)
    {
        var policy = await store.ReadAsync(ct);
        var definitions = await tasks.GetTaskCatalogAsync(ct, applyManagementPolicy: false);
        var cutoff = DateTimeOffset.UtcNow - runtimeOptions.StaleAfter;
        var reported = await database.EnginePolicyObservations.AsNoTracking()
            .Where(engine => engine.LastSeenAt >= cutoff).Select(engine => engine.ServingVersionIdsJson).ToListAsync(ct);
        var serving = reported.SelectMany(json => JsonSerializer.Deserialize<int[]>(json) ?? []).ToHashSet();
        return Ok(new
        {
            policy.Revision,
            Plugins = definitions.OrderBy(plugin => plugin.DisplayName ?? plugin.Name).Select(plugin => new
            {
                plugin.Id, plugin.Name, plugin.DisplayName, Enabled = !policy.DisabledPlugins.Contains(plugin.Id),
                Versions = plugin.Versions.OrderByDescending(version => Version.Parse(version.Version)).Select(version =>
                {
                    var disabled = policy.IsDisabled(plugin.Id, version.Id);
                    return new
                    {
                        version.Id, version.Version, Lane = Version.Parse(version.Version).ToString(2),
                        Enabled = !policy.DisabledVersions.Contains(version.Id), EffectiveEnabled = !disabled,
                        Runnable = !disabled && version.RuntimeAvailability.Runnable, Serving = serving.Contains(version.Id),
                        Status = disabled ? nameof(PluginTaskVersionRuntimeStatus.DisabledByOperator) : version.RuntimeAvailability.Status
                    };
                })
            })
        });
    }

    [HttpGet("fleet")]
    public async Task<IActionResult> GetFleet(CancellationToken ct)
    {
        var policy = await store.ReadAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - runtimeOptions.StaleAfter;
        var engines = await database.EnginePolicyObservations.AsNoTracking()
            .Where(engine => engine.LastSeenAt >= cutoff).OrderBy(engine => engine.EngineId)
            .Select(engine => new { engine.EngineId, engine.AppliedRevision, engine.LastSeenAt, engine.FailureCode })
            .ToListAsync(ct);
        var status = engines.Count == 0 ? "Awaiting an Engine" :
            engines.All(engine => engine.AppliedRevision == policy.Revision && engine.FailureCode == null) ? "Applied" : "Applying";
        var writesEnabled = HttpContext.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("EngineManagement:WritesEnabled", true);
        return Ok(new { policy.Revision, Status = status, Engines = engines, WritesEnabled = writesEnabled });
    }

    [HttpPut("settings/{key}")]
    public async Task<IActionResult> SetSetting(string key, SetSettingRequest request, CancellationToken ct)
    {
        var field = catalog.Find(key);
        if (field is null) return NotFound();
        if (!field.Accepts(request.Value))
            return BadRequest(new { Message = $"Supply a valid {field.Kind} value for this setting." });
        var setting = new ManagedSetting(request.Value, null);
        if (field.Secret)
        {
            if ((await store.ReadAsync(ct)).Revision != request.ExpectedRevision) return RevisionConflict();
            try
            {
                var secrets = HttpContext.RequestServices.GetRequiredService<IManagementSecretStore>();
                setting = new(null, await secrets.WriteAsync(key, request.Value, ct));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Provider errors may contain confidential request details.
                return StatusCode(503, new { Message = "The credential store is unavailable. The override was not changed." });
            }
        }
        return await Save(() => store.SetSettingAsync(request.ExpectedRevision, actors.ResolveCurrentActor().Id,
            key, setting, ct));
    }

    [HttpDelete("settings/{key}")]
    public async Task<IActionResult> RemoveSetting(string key, [FromQuery, BindRequired] long expectedRevision, CancellationToken ct)
    {
        if (catalog.Find(key) is null) return NotFound();
        return await Save(() => store.SetSettingAsync(expectedRevision, actors.ResolveCurrentActor().Id, key, null, ct));
    }

    [HttpPut("plugins/{id:int}")]
    public async Task<IActionResult> SetPluginEnabled(int id, SetEnabledRequest request, CancellationToken ct)
    {
        if (!await database.TaskDefinitions.AnyAsync(plugin => plugin.Id == id, ct)) return NotFound();
        return await Save(() => store.SetEnabledAsync(request.ExpectedRevision, actors.ResolveCurrentActor().Id,
            id, null, request.Enabled, ct));
    }

    [HttpPut("plugins/{id:int}/versions/{versionId:int}")]
    public async Task<IActionResult> SetVersionEnabled(int id, int versionId, SetEnabledRequest request, CancellationToken ct)
    {
        if (!await database.TaskVersions.AnyAsync(v => v.Id == versionId && v.TaskDefinitionId == id, ct)) return NotFound();
        return await Save(() => store.SetEnabledAsync(request.ExpectedRevision, actors.ResolveCurrentActor().Id,
            id, versionId, request.Enabled, ct));
    }

    private async Task<IActionResult> Save(Func<Task<long>> commit)
    {
        try { return Ok(new { Revision = await commit(), Status = "Saved" }); }
        catch (EnginePolicyConflictException) { return RevisionConflict(); }
    }

    private ConflictObjectResult RevisionConflict() => Conflict(new { Message = "The fleet policy has changed. Reload and retry." });

    public sealed class SetSettingRequest
    {
        public required long ExpectedRevision { get; init; }
        public required string Value { get; init; }
    }

    public sealed class SetEnabledRequest
    {
        public required long ExpectedRevision { get; init; }
        public required bool Enabled { get; init; }
    }
}

public sealed class ManagementAccessFilter(IConfiguration configuration, IOptions<WorkflowAuthenticationOptions> auth)
    : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var identity = context.HttpContext.User.Identity;
        if (identity?.IsAuthenticated != true || identity.AuthenticationType == WorkflowAuthenticationSchemes.Development ||
            (!auth.Value.Enabled && identity.AuthenticationType != WorkflowAuthenticationSchemes.ManagementBearer))
            context.Result = new UnauthorizedResult();
        else if (!configuration.GetValue<bool>("EngineManagement:Enabled"))
            context.Result = new NotFoundResult();
        else if (!HttpMethods.IsGet(context.HttpContext.Request.Method) &&
                 !configuration.GetValue("EngineManagement:WritesEnabled", true))
            context.Result = new ObjectResult(new { Message = "Management writes are disabled during rollout." })
                { StatusCode = StatusCodes.Status503ServiceUnavailable };
    }
}

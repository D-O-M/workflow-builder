using Backend.API.Services;
using Backend.API.Infrastructure;
using Backend.Repositories;
using Backend.Security.Actors;
using Backend.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/process-recoveries")]
[Authorize(Policy = WorkflowAuthorizationPolicies.ProcessOperator)]
[TypeFilter(typeof(ProcessRecoveryFilter))]
public sealed class ProcessRecoveriesController(ProcessRecoveryService service, IWorkflowActorResolver actors) : ControllerBase
{
    private string Actor => actors.ResolveCurrentActor().Id;

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(RecoverySelection selection, CancellationToken ct) =>
        Ok(await service.PageAsync(await service.PreviewAsync(selection, Actor, ct), 0, 25, ct));

    [HttpPut("{id:guid}/preview")]
    public async Task<IActionResult> Revise(Guid id, RecoveryRevision revision, CancellationToken ct) =>
        Ok(await service.PageAsync(await service.ReviseAsync(id, revision, Actor, ct), 0, 25, ct));

    [HttpPost("{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, RecoveryConfirmation confirmation, CancellationToken ct) =>
        AcceptedAtAction(nameof(Read), new { id },
            await service.PageAsync(await service.ConfirmAsync(id, confirmation, Actor, ct), 0, 25, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct, int offset = 0, int limit = 25, int? revision = null) =>
        Ok(await service.PageAsync(await service.GetRevisionAsync(id, revision, ct), offset, limit, ct));

    [HttpGet]
    public async Task<IActionResult> History(CancellationToken ct, Guid? processId = null, int offset = 0, int limit = 25) =>
        Ok(await service.HistoryAsync(processId, offset, limit, ct));
}

public sealed class ProcessRecoveryFilter(ProcessRecoveryAvailability availability) : IAuthorizationFilter, IExceptionFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!availability.Enabled) context.Result = new NotFoundResult();
    }

    public void OnException(ExceptionContext context)
    {
        var status = context.Exception switch
        {
            ArgumentException => 400,
            EntityNotFoundException => 404,
            RecoveryConflictException => 409,
            _ => 0
        };
        if (status == 0) return;
        context.Result = new ObjectResult(new { error = context.Exception.Message }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}

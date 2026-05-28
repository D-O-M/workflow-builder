using Backend.Security.Actors;

namespace Backend.Security.Authorization;

public interface IWorkflowAuthorizationService
{
    ValueTask<AuthorizationDecision> AuthorizeAsync(
        WorkflowActor actor,
        string action,
        object? resource = null,
        CancellationToken cancellationToken = default);
}

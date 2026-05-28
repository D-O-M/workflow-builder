using Backend.Security.Actors;

namespace Backend.Security.Authorization;

public sealed class CoarseWorkflowAuthorizationService : IWorkflowAuthorizationService
{
    private static readonly IReadOnlyDictionary<string, string[]> RoleRequirements =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [WorkflowAuthorizationActions.BlueprintCreate] = [WorkflowRoles.BlueprintAuthor, WorkflowRoles.Admin],
            [WorkflowAuthorizationActions.BlueprintUpdate] = [WorkflowRoles.BlueprintAuthor, WorkflowRoles.Admin],
            [WorkflowAuthorizationActions.BlueprintVersionCreate] = [WorkflowRoles.BlueprintAuthor, WorkflowRoles.Admin],
            [WorkflowAuthorizationActions.BlueprintImport] = [WorkflowRoles.BlueprintAuthor, WorkflowRoles.Admin],
            [WorkflowAuthorizationActions.BlueprintExport] = [WorkflowRoles.BlueprintAuthor, WorkflowRoles.Admin],
            [WorkflowAuthorizationActions.ProcessStart] = [WorkflowRoles.ProcessOperator, WorkflowRoles.Admin],
            [WorkflowAuthorizationActions.PhasedBlueprintCreate] = [WorkflowRoles.BlueprintAuthor, WorkflowRoles.Admin],
            [WorkflowAuthorizationActions.PhasedBlueprintPhaseTrigger] = [WorkflowRoles.ProcessOperator, WorkflowRoles.Admin],
            [WorkflowAuthorizationActions.PluginUse] = [WorkflowRoles.ProcessOperator, WorkflowRoles.Admin]
        };

    public ValueTask<AuthorizationDecision> AuthorizeAsync(
        WorkflowActor actor,
        string action,
        object? resource = null,
        CancellationToken cancellationToken = default)
    {
        if (!actor.IsAuthenticated)
        {
            return ValueTask.FromResult(AuthorizationDecision.Deny("An authenticated actor is required."));
        }

        if (!RoleRequirements.TryGetValue(action, out var allowedRoles))
        {
            return ValueTask.FromResult(AuthorizationDecision.Allow());
        }

        return allowedRoles.Any(actor.IsInRole)
            ? ValueTask.FromResult(AuthorizationDecision.Allow())
            : ValueTask.FromResult(AuthorizationDecision.Deny($"Action '{action}' requires one of: {string.Join(", ", allowedRoles)}."));
    }
}

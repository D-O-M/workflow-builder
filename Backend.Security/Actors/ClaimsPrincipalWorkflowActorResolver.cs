using System.Security.Claims;
using Backend.Security.Authentication;
using Backend.Security.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Backend.Security.Actors;

public sealed class ClaimsPrincipalWorkflowActorResolver : IWorkflowActorResolver
{
    private const string DevelopmentActorId = "local-development";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptions<WorkflowAuthenticationOptions> _options;

    public ClaimsPrincipalWorkflowActorResolver(
        IHttpContextAccessor httpContextAccessor,
        IOptions<WorkflowAuthenticationOptions> options)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options;
    }

    public WorkflowActor ResolveCurrentActor() =>
        Resolve(_httpContextAccessor.HttpContext?.User);

    public WorkflowActor Resolve(ClaimsPrincipal? principal)
    {
        if (!_options.Value.Enabled)
        {
            return DevelopmentActor();
        }

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return new WorkflowActor(
                ActorType.Anonymous,
                "anonymous",
                "Anonymous",
                Email: null,
                Roles: new HashSet<string>(StringComparer.Ordinal));
        }

        var id =
            principal.FindFirstValue("sub") ??
            principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
            principal.Identity.Name ??
            "authenticated-user";

        var displayName =
            principal.FindFirstValue("preferred_username") ??
            principal.FindFirstValue(ClaimTypes.Name) ??
            principal.FindFirstValue("name") ??
            id;

        var roles = principal
            .FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);

        return new WorkflowActor(
            ActorType.Human,
            id,
            displayName,
            principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email"),
            roles);
    }

    private static WorkflowActor DevelopmentActor() =>
        new(
            ActorType.System,
            DevelopmentActorId,
            "Local Development",
            Email: null,
            Roles: WorkflowRoles.DevelopmentRoles.ToHashSet(StringComparer.Ordinal));
}

using System.Security.Claims;

namespace Backend.Security.Actors;

public interface IWorkflowActorResolver
{
    WorkflowActor ResolveCurrentActor();

    WorkflowActor Resolve(ClaimsPrincipal? principal);
}

namespace Backend.Security.Actors;

public sealed record WorkflowActor(
    ActorType Type,
    string Id,
    string DisplayName,
    string? Email,
    IReadOnlySet<string> Roles)
{
    public bool IsAuthenticated => Type != ActorType.Anonymous;

    public bool IsInRole(string role) => Roles.Contains(role);
}

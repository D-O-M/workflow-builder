namespace Backend.Security.Authorization;

public sealed record AuthorizationDecision(bool Allowed, string? Reason = null)
{
    public static AuthorizationDecision Allow() => new(true);

    public static AuthorizationDecision Deny(string reason) => new(false, reason);
}

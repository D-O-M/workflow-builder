namespace Backend.Security.Cors;

public static class WorkflowClientOriginPolicy
{
    public const string PolicyName = "WorkflowBuilderClient";
    public const string AllowedOriginsSectionName = "Cors:AllowedOrigins";

    public static bool IsAllowed(string origin, IReadOnlyCollection<string> configuredOrigins)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var normalizedOrigin = Normalize(origin);
        if (configuredOrigins.Any(configured =>
                !string.IsNullOrWhiteSpace(configured)
                && string.Equals(Normalize(configured), normalizedOrigin, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return uri.IsLoopback
            || string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string origin) => origin.Trim().TrimEnd('/');
}

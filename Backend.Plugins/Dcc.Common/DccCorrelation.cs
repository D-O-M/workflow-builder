namespace Dcc.Common;

public static class DccCorrelation
{
    public static string BuildKey(
        string? requestId,
        string? serviceReference,
        string? serviceReferenceVariant,
        string? commandVariant)
    {
        var parts = new[]
        {
            DccIdentifiers.Normalize(requestId, "unknown-request"),
            DccIdentifiers.Normalize(serviceReference, "unknown-service"),
            DccIdentifiers.Normalize(serviceReferenceVariant, "unknown-variant"),
            DccIdentifiers.Normalize(commandVariant, "unknown-command-variant")
        };

        return string.Join("|", parts);
    }
}

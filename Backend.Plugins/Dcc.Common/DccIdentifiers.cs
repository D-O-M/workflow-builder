namespace Dcc.Common;

public static class DccIdentifiers
{
    private const string DemoOriginatorEui = "00-00-00-00-00-00-00-01";
    private const string DemoRecipientEui = "00-00-00-00-00-00-00-02";

    public static string ResolveRequestId(string? requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            return requestId.Trim();
        }

        var counter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{DemoOriginatorEui}:{DemoRecipientEui}:{counter}";
    }

    public static string Normalize(string? value, string fallback = "") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

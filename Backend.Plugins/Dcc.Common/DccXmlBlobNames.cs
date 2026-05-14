using System.Text;

namespace Dcc.Common;

public static class DccXmlBlobNames
{
    public static string Request(string correlationKey, string requestId, DateTimeOffset? timestamp = null) =>
        BuildName("requests", correlationKey, requestId, timestamp);

    public static string Response(string correlationKey, string requestId, DateTimeOffset? timestamp = null) =>
        BuildName("responses", correlationKey, requestId, timestamp);

    private static string BuildName(
        string kind,
        string correlationKey,
        string requestId,
        DateTimeOffset? timestamp)
    {
        var instant = timestamp ?? DateTimeOffset.UtcNow;
        var safeKey = ToSafePathSegment(string.IsNullOrWhiteSpace(correlationKey) ? requestId : correlationKey);
        var safeRequest = ToSafePathSegment(requestId);
        return $"dcc/{kind}/{safeKey}/{instant:yyyyMMddHHmmss}-{safeRequest}.xml";
    }

    public static string ToSafePathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '-');
        }

        var safe = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "dcc-message" : safe;
    }
}

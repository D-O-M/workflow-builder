using System.Text.RegularExpressions;
using Backend.Database.State.Objects;
using Backend.Models;

namespace Backend.Interfaces.Repositories;

public interface IPluginIngestionStatusStore
{
    Task AppendEventAsync(
        PluginIngestionStatusUpdate update,
        CancellationToken cancellationToken);

    Task<bool> IsQuarantinedAsync(
        string blobName,
        CancellationToken cancellationToken);

    Task QuarantineAsync(
        string id,
        string? reason,
        CancellationToken cancellationToken);

    Task UnquarantineAsync(
        string id,
        string? reason,
        CancellationToken cancellationToken);

    Task<PluginIngestionStatusRecord?> GetAsync(
        string id,
        CancellationToken cancellationToken);

    Task<PluginIngestionStatusPage> QueryAsync(
        PluginIngestionStatusQuery query,
        CancellationToken cancellationToken);
}

public static partial class PluginIngestionStatusIds
{
    public static string FromPlugin(PluginBlobInfo pluginInfo) =>
        FromBlobName(pluginInfo.BlobName);

    public static string FromPlugin(string pluginName, Version pluginVersion) =>
        FromBlobName($"{pluginName}_{pluginVersion}.zip");

    public static string FromBlobName(string blobName)
    {
        var normalized = string.IsNullOrWhiteSpace(blobName)
            ? "<unknown>"
            : blobName.Trim().ToLowerInvariant();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return "plugin-bundle-" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public static partial class PluginIngestionStatusMessageSanitizer
{
    private const int MaxLength = 768;

    public static string? Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var sanitized = message.Trim();
        sanitized = WindowsPathRegex().Replace(sanitized, "<path>");
        sanitized = UnixPathRegex().Replace(sanitized, "<path>");
        sanitized = SecretAssignmentRegex().Replace(sanitized, match => $"{match.Groups["key"].Value}=<redacted>");
        sanitized = SecretUriRegex().Replace(sanitized, match => $"{match.Groups["key"].Value}=<redacted>");
        sanitized = sanitized.ReplaceLineEndings(" ");

        while (sanitized.Contains("  ", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (sanitized.Length <= MaxLength)
        {
            return sanitized;
        }

        return sanitized[..MaxLength] + "...";
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])([A-Za-z]:\\|\\\\)[^\s,;""']+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])/(?:tmp|var|home|Users|workspace|mnt|app|data)[^\s,;""']*", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"(?<key>(?:AccountKey|SharedAccessKey|Password|Pwd|Secret|Token|ApiKey|AccessKey))\s*=\s*[^;\s,]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?<key>(?:sig|token|code|client_secret|password))=[^&\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretUriRegex();
}

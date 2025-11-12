using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Backend.PluginLoader.Services;

internal interface IPluginBlobNameParser
{
    PluginBlobInfo Parse(string blobName);
}

internal sealed class PluginBlobNameParser : IPluginBlobNameParser
{
    private static readonly Regex BlobNamePattern = new(
        @"^(?<name>.+)_(?<version>\d+(\.\d+){0,3})\.zip$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<PluginBlobNameParser> _logger;

    public PluginBlobNameParser(ILogger<PluginBlobNameParser> logger)
    {
        _logger = logger;
    }

    public PluginBlobInfo Parse(string blobName)
    {
        var match = BlobNamePattern.Match(blobName);
        if (!match.Success)
        {
            _logger.LogError("Blob name {BlobName} is not in the expected format {{Name}}_{{Version}}.zip", blobName);
            throw new FormatException("Blob name must be {Name}_{Version}.zip");
        }

        var name = match.Groups["name"].Value;
        var versionString = match.Groups["version"].Value;
        if (!Version.TryParse(versionString, out var version))
        {
            _logger.LogError("Unable to parse version {Version} for blob {BlobName}", versionString, blobName);
            throw new FormatException($"Invalid version format encountered while processing blob name. Name: {name} | Version: {versionString}");
        }

        return new PluginBlobInfo(blobName, name, version);
    }
}

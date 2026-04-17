using System.IO.Compression;
using Backend.Interfaces.Utilities;
using Backend.Models;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class FilePluginBundleSource : IPluginBundleSource
{
    private readonly PluginBundleCatalog _bundleCatalog;

    public FilePluginBundleSource(PluginBundleCatalog bundleCatalog)
    {
        _bundleCatalog = bundleCatalog;
    }

    public Task ExtractBundleAsync(
        PluginBlobInfo pluginInfo,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Directory.Exists(destinationDirectory))
        {
            Directory.Delete(destinationDirectory, recursive: true);
        }

        Directory.CreateDirectory(destinationDirectory);
        ZipFile.ExtractToDirectory(_bundleCatalog[pluginInfo.PluginName], destinationDirectory, overwriteFiles: true);
        return Task.CompletedTask;
    }
}

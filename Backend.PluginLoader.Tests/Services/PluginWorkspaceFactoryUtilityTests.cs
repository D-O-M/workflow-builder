using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backend.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.PluginLoader.Tests.Services;

public sealed class PluginWorkspaceFactoryUtilityTests : IDisposable
{
    private readonly PluginWorkspaceFactoryUtility _factory;
    private readonly List<string> _createdDirectories = new();

    public PluginWorkspaceFactoryUtilityTests()
    {
        _factory = new PluginWorkspaceFactoryUtility(
            new NullLogger<PluginWorkspaceFactoryUtility>());
    }

    [Test]
    public async Task CreateWorkspaceAsync_SanitizesPluginName()
    {
        var path = await _factory.CreateWorkspaceAsync(
            "plugin:name",
            new Version(1, 0),
            CancellationToken.None);

        _createdDirectories.Add(path);

        Path.GetFileName(path).ShouldContain("plugin_name");
        Directory.Exists(path).ShouldBeTrue();
    }

    public void Dispose()
    {
        foreach (var directory in _createdDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // ignored
            }
        }
    }
}

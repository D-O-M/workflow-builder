using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backend.PluginLoader.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using TUnit.Core;

namespace Backend.PluginLoader.Tests.Services;

public sealed class PluginWorkspaceFactoryTests : IDisposable
{
    private readonly PluginWorkspaceFactory _factory;
    private readonly List<string> _createdDirectories = new();

    public PluginWorkspaceFactoryTests()
    {
        _factory = new PluginWorkspaceFactory(Mock.Of<ILogger<PluginWorkspaceFactory>>());
    }

    [Test]
    public async Task CreateWorkspaceAsync_SanitizesPluginName()
    {
        var path = await _factory.CreateWorkspaceAsync("plugin:name", new Version(1, 0), CancellationToken.None);
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

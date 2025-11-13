using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.DTOs;
using Backend.Interfaces.Services;
using Backend.PluginLoader.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Backend.PluginLoader.Tests.Services;

public class PluginLoaderServiceTests
{
    [Test]
    public async Task ExecuteCycleAsync_ProcessesAllPendingPlugins()
    {
        var discovery = new Mock<IPluginDiscoveryService>();
        var processor = new Mock<IPluginBundleProcessor>();
        var plugins = new[]
        {
            new PluginBlobInfo("PluginA_1.0.0.zip", "PluginA", new Version(1, 0, 0)),
            new PluginBlobInfo("PluginB_2.0.0.zip", "PluginB", new Version(2, 0, 0))
        };

        discovery.Setup(d => d.GetNewPluginsAsync(CancellationToken.None))
            .ReturnsAsync(plugins);

        var service = new PluginLoaderService(
            discovery.Object,
            processor.Object,
            new NullLogger<PluginLoaderService>(),
            TimeSpan.Zero);

        await service.ExecuteCycleAsync(CancellationToken.None);

        processor.Verify(
            p => p.ProcessAsync(plugins[0], CancellationToken.None),
            Times.Once);

        processor.Verify(
            p => p.ProcessAsync(plugins[1], CancellationToken.None),
            Times.Once);
    }

    [Test]
    public async Task ExecuteCycleAsync_ContinuesWhenPluginProcessingFails()
    {
        var discovery = new Mock<IPluginDiscoveryService>();
        var processor = new Mock<IPluginBundleProcessor>();
        var plugins = new[]
        {
            new PluginBlobInfo("PluginA_1.0.0.zip", "PluginA", new Version(1, 0, 0)),
            new PluginBlobInfo("PluginB_2.0.0.zip", "PluginB", new Version(2, 0, 0))
        };

        discovery.Setup(d => d.GetNewPluginsAsync(CancellationToken.None))
            .ReturnsAsync(plugins);

        processor.Setup(p => p.ProcessAsync(plugins[0], CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException("Processing failed"));

        var service = new PluginLoaderService(
            discovery.Object,
            processor.Object,
            new NullLogger<PluginLoaderService>(),
            TimeSpan.Zero);

        await service.ExecuteCycleAsync(CancellationToken.None);

        processor.Verify(
            p => p.ProcessAsync(plugins[1], CancellationToken.None),
            Times.Once);
    }
}

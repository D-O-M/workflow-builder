using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backend.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.PluginLoader.Tests.Services;

public sealed class PluginSchemaAggregationUtilityTests : IDisposable
{
    private readonly PluginSchemaAggregationUtility _aggregator;
    private readonly string _workspaceDirectory;

    public PluginSchemaAggregationUtilityTests()
    {
        _aggregator = new PluginSchemaAggregationUtility(
            new NullLogger<PluginSchemaAggregationUtility>());
        _workspaceDirectory = Path.Combine(
            Path.GetTempPath(),
            $"schema_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceDirectory);
    }

    [Test]
    public async Task LoadAsync_WithSchemasJson_ReturnsAggregate()
    {
        var schemasPath = Path.Combine(_workspaceDirectory, "schemas.json");
        await File.WriteAllTextAsync(schemasPath,
            """
            
            {
                "components": {
                    "schemas": {
                        "Sample": {
                            "type": ["object"],
                            "properties": {
                                "value": {
                                    "type": "string"
                                }
                            }
                        }
                    }
                }
            }
            """);

        var result = await _aggregator.LoadAsync(
            _workspaceDirectory,
            CancellationToken.None);

        result.ShouldNotBeNull();
        result.DefinitionCount.ShouldBe(1);
        result.TypedSchemas.ShouldNotBeNull();
    }

    [Test]
    public async Task LoadAsync_WithSchemasFolder_AggregatesFiles()
    {
        var folder = Path.Combine(_workspaceDirectory, "schemas");
        Directory.CreateDirectory(folder);

        await File.WriteAllTextAsync(
            Path.Combine(folder, "alpha.json"),
            """{"type":"object"}""");

        await File.WriteAllTextAsync(
            Path.Combine(folder, "beta.json"),
            """{"type":"object"}""");

        var result = await _aggregator.LoadAsync(
            _workspaceDirectory,
            CancellationToken.None);

        result.ShouldNotBeNull();
        result.DefinitionCount.ShouldBe(2);
    }

    [Test]
    public async Task LoadAsync_WithNoSchemas_ReturnsNull()
    {
        var result = await _aggregator.LoadAsync(
            _workspaceDirectory,
            CancellationToken.None);
        result.ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceDirectory))
        {
            Directory.Delete(_workspaceDirectory, recursive: true);
        }
    }
}

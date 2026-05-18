using System;
using System.Collections.Generic;
using Backend.Database.Relational.Entities;
using Backend.Engine;
using Backend.Models;
using Backend.PluginAbi;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class PluginRuntimeCatalogTests
{
    [Test]
    public void TryResolvePluginRuntime_returns_configured_route_for_lane()
    {
        var catalog = new PluginRuntimeCatalog();
        var routedVersion = new TaskDefinitionInfo(
            100,
            10,
            "Weather",
            "weather.exe",
            new Version(1, 2, 3),
            TaskVersionState.Active,
            PluginAbiDefaults.CurrentMetadata);

        catalog.ReplaceRoutes(
            [routedVersion],
            new Dictionary<PluginRuntimeLane, TaskDefinitionInfo>
            {
                [new PluginRuntimeLane(10, 1, 2)] = routedVersion
            });

        catalog.TryResolvePluginRuntime(10, 1, 2, out var resolution).ShouldBeTrue();
        resolution.TaskVersionId.ShouldBe(100);
        resolution.PluginName.ShouldBe("Weather");
        resolution.Version.ShouldBe(new Version(1, 2, 3));
    }

    [Test]
    public void TryResolvePluginRuntime_returns_false_for_unrouted_lane()
    {
        var catalog = new PluginRuntimeCatalog();

        catalog.TryResolvePluginRuntime(10, 1, 2, out _).ShouldBeFalse();
    }
}

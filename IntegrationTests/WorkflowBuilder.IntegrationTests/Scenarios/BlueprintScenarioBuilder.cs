using System.Net.Http.Json;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Shouldly;
using WorkflowBuilder.IntegrationTests.Infrastructure;

namespace WorkflowBuilder.IntegrationTests.Scenarios;

internal static class BlueprintScenarioBuilder
{
    public sealed record BlueprintScenario(
        int BlueprintDefinitionId,
        int BlueprintVersionId,
        Guid FirstBlueprintNodeId,
        Guid MiddleBlueprintNodeId,
        Guid FinalBlueprintNodeId);

    public static Task<BlueprintScenario> CreateHappyPathAsync(
        DistributedAppTestEnvironment environment,
        CancellationToken cancellationToken) =>
        CreateVersionedBlueprintAsync(
            environment,
            $"{PluginBundleCatalog.SeedPlugin}-happy-{Guid.NewGuid():N}",
            [
                PluginBundleCatalog.SeedPlugin,
                PluginBundleCatalog.TransformPlugin,
                PluginBundleCatalog.ResultPlugin
            ],
            cancellationToken);

    public static Task<BlueprintScenario> CreateFailurePathAsync(
        DistributedAppTestEnvironment environment,
        CancellationToken cancellationToken) =>
        CreateVersionedBlueprintAsync(
            environment,
            $"{PluginBundleCatalog.SeedPlugin}-failure-{Guid.NewGuid():N}",
            [
                PluginBundleCatalog.SeedPlugin,
                PluginBundleCatalog.FailurePlugin,
                PluginBundleCatalog.ResultPlugin
            ],
            cancellationToken);

    private static async Task<BlueprintScenario> CreateVersionedBlueprintAsync(
        DistributedAppTestEnvironment environment,
        string blueprintName,
        string[] pluginNames,
        CancellationToken cancellationToken)
    {
        var tasks = await environment.GetTasksAsync(cancellationToken);

        var firstTaskVersion = GetTaskVersion(tasks, pluginNames[0], environment.Settings.RunVersion);
        var middleTaskVersion = GetTaskVersion(tasks, pluginNames[1], environment.Settings.RunVersion);
        var finalTaskVersion = GetTaskVersion(tasks, pluginNames[2], environment.Settings.RunVersion);

        var firstTaskIdV1 = Guid.NewGuid();
        var middleTaskIdV1 = Guid.NewGuid();
        var finalTaskIdV1 = Guid.NewGuid();

        var initialBlueprint = new BlueprintInputModel
        {
            Name = blueprintName,
            Description = "Integration test blueprint",
            StartingBlueprintNodeId = firstTaskIdV1,
            Tasks =
            [
                CreateTask(firstTaskIdV1, firstTaskVersion.TaskDefinitionId, firstTaskVersion.Version, 100, 120, middleTaskIdV1),
                CreateTask(middleTaskIdV1, middleTaskVersion.TaskDefinitionId, middleTaskVersion.Version, 420, 120, finalTaskIdV1),
                CreateTask(finalTaskIdV1, finalTaskVersion.TaskDefinitionId, finalTaskVersion.Version, 740, 120)
            ]
        };

        var createResponse = await environment.ApiClient.PostAsJsonAsync(
            "/api/blueprints",
            initialBlueprint,
            cancellationToken);

        createResponse.IsSuccessStatusCode.ShouldBeTrue(await createResponse.Content.ReadAsStringAsync(cancellationToken));

        var createdBlueprint = await createResponse.Content.ReadFromJsonAsync<BlueprintViewModel>(cancellationToken: cancellationToken);
        createdBlueprint.ShouldNotBeNull();

        var firstTaskIdV2 = Guid.NewGuid();
        var middleTaskIdV2 = Guid.NewGuid();
        var finalTaskIdV2 = Guid.NewGuid();

        var nextVersion = new BlueprintVersionInputModel
        {
            BlueprintDefinitionId = createdBlueprint.Id,
            BaseVersion = 1,
            Description = "Versioned integration test blueprint",
            StartingBlueprintNodeId = firstTaskIdV2,
            Tasks =
            [
                CreateTask(firstTaskIdV2, firstTaskVersion.TaskDefinitionId, firstTaskVersion.Version, 120, 160, middleTaskIdV2),
                CreateTask(middleTaskIdV2, middleTaskVersion.TaskDefinitionId, middleTaskVersion.Version, 460, 160, finalTaskIdV2),
                CreateTask(finalTaskIdV2, finalTaskVersion.TaskDefinitionId, finalTaskVersion.Version, 780, 160)
            ]
        };

        var versionResponse = await environment.ApiClient.PostAsJsonAsync(
            $"/api/blueprints/{createdBlueprint.Id}/versions",
            nextVersion,
            cancellationToken);

        versionResponse.IsSuccessStatusCode.ShouldBeTrue(await versionResponse.Content.ReadAsStringAsync(cancellationToken));

        var createdVersion = await versionResponse.Content.ReadFromJsonAsync<BlueprintVersionViewModel>(cancellationToken: cancellationToken);
        createdVersion.ShouldNotBeNull();

        return new BlueprintScenario(
            createdBlueprint.Id,
            createdVersion.BlueprintVersionId,
            firstTaskIdV2,
            middleTaskIdV2,
            finalTaskIdV2);
    }

    private static BlueprintNodeInputModel CreateTask(
        Guid instanceId,
        int taskDefinitionId,
        string version,
        int x,
        int y,
        Guid? nextBlueprintNodeId = null)
    {
        var (allowedMajor, allowedMinor) = GetVersionLane(version);
        var task = new BlueprintNodeInputModel
        {
            InstanceId = instanceId,
            TaskDefinitionId = taskDefinitionId,
            AllowedMajor = allowedMajor,
            AllowedMinor = allowedMinor,
            Position = new CoordinatesViewModel { X = x, Y = y }
        };

        if (nextBlueprintNodeId is not null)
        {
            task.OutgoingConnections.Add(new BlueprintConnectionInputModel
            {
                ChannelName = "Success",
                ConnectedBlueprintNodeId = nextBlueprintNodeId.Value,
                Anchor = 0,
                Side = "right",
                Position = 0
            });
        }

        return task;
    }

    private static (int TaskDefinitionId, string Version) GetTaskVersion(
        IEnumerable<TaskDefinitionViewModel> tasks,
        string pluginName,
        string version)
    {
        var taskDefinition = tasks.Single(task => task.Name == pluginName);
        var taskVersion = taskDefinition.Versions
            .Single(versionViewModel => VersionsMatch(versionViewModel.Version, version));

        return (taskDefinition.Id, taskVersion.Version);
    }

    private static bool VersionsMatch(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.Equals(rightVersion);
        }

        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static (int Major, int Minor) GetVersionLane(string version)
    {
        var parsed = Version.Parse(version);
        return (parsed.Major, parsed.Minor);
    }
}

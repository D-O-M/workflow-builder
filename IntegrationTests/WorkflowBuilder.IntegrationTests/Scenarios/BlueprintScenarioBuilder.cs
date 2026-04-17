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
        Guid FirstTaskInstanceId,
        Guid MiddleTaskInstanceId,
        Guid FinalTaskInstanceId);

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

        var firstTaskVersionId = GetVersionId(tasks, pluginNames[0], environment.Settings.RunVersion);
        var middleTaskVersionId = GetVersionId(tasks, pluginNames[1], environment.Settings.RunVersion);
        var finalTaskVersionId = GetVersionId(tasks, pluginNames[2], environment.Settings.RunVersion);

        var firstTaskIdV1 = Guid.NewGuid();
        var middleTaskIdV1 = Guid.NewGuid();
        var finalTaskIdV1 = Guid.NewGuid();

        var initialBlueprint = new BlueprintInputModel
        {
            Name = blueprintName,
            Description = "Integration test blueprint",
            StartingTaskInstanceId = firstTaskIdV1,
            Tasks =
            [
                CreateTask(firstTaskIdV1, firstTaskVersionId, 100, 120, middleTaskIdV1),
                CreateTask(middleTaskIdV1, middleTaskVersionId, 420, 120, finalTaskIdV1),
                CreateTask(finalTaskIdV1, finalTaskVersionId, 740, 120)
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
            Name = $"{blueprintName} v2",
            Description = "Versioned integration test blueprint",
            StartingTaskInstanceId = firstTaskIdV2,
            Tasks =
            [
                CreateTask(firstTaskIdV2, firstTaskVersionId, 120, 160, middleTaskIdV2),
                CreateTask(middleTaskIdV2, middleTaskVersionId, 460, 160, finalTaskIdV2),
                CreateTask(finalTaskIdV2, finalTaskVersionId, 780, 160)
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

    private static BlueprintTaskInputModel CreateTask(
        Guid instanceId,
        int versionId,
        int x,
        int y,
        Guid? nextTaskInstanceId = null)
    {
        var task = new BlueprintTaskInputModel
        {
            InstanceId = instanceId,
            VersionId = versionId,
            Position = new CoordinatesViewModel { X = x, Y = y }
        };

        if (nextTaskInstanceId is not null)
        {
            task.OutgoingConnections.Add(new BlueprintTaskConnectionInputModel
            {
                ChannelName = "Success",
                ConnectedTaskInstanceId = nextTaskInstanceId.Value,
                Anchor = 0,
                Side = "right",
                Position = 0
            });
        }

        return task;
    }

    private static int GetVersionId(
        IEnumerable<TaskDefinitionViewModel> tasks,
        string pluginName,
        string version)
    {
        return tasks
            .Single(task => task.Name == pluginName)
            .Versions
            .Single(taskVersion => VersionsMatch(taskVersion.Version, version))
            .Id;
    }

    private static bool VersionsMatch(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.Equals(rightVersion);
        }

        return string.Equals(left, right, StringComparison.Ordinal);
    }
}

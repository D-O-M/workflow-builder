using System.Net.Http.Json;
using System.Text.Json;
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

    public sealed record FanOutScenario(
        int BlueprintDefinitionId,
        int BlueprintVersionId,
        Guid SeedBlueprintNodeId,
        Guid LeftBlueprintNodeId,
        Guid RightBlueprintNodeId);

    public sealed record JoinScenario(
        int BlueprintDefinitionId,
        int BlueprintVersionId,
        Guid SeedBlueprintNodeId,
        Guid LeftBlueprintNodeId,
        Guid RightBlueprintNodeId,
        Guid JoinBlueprintNodeId);

    private sealed record TaskVersionReference(int TaskDefinitionId, string Version);

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
            middleDesignTimeOverrides: null,
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
            middleDesignTimeOverrides: CreateOverrides(("ShouldFail", true)),
            cancellationToken);

    public static async Task<FanOutScenario> CreateFanOutScenarioAsync(
        DistributedAppTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        var tasks = await environment.GetTasksAsync(cancellationToken);
        var seedTaskVersion = GetTaskVersion(tasks, PluginBundleCatalog.SeedPlugin, environment.Settings.RunVersion);
        var leftTaskVersion = GetTaskVersion(tasks, PluginBundleCatalog.TransformPlugin, environment.Settings.RunVersion);
        var rightTaskVersion = GetTaskVersion(tasks, PluginBundleCatalog.TransformPlugin, environment.Settings.RunVersion);

        var seedNodeId = Guid.NewGuid();
        var leftNodeId = Guid.NewGuid();
        var rightNodeId = Guid.NewGuid();

        var createdVersion = await CreateBlueprintAsync(
            environment,
            new BlueprintInputModel
            {
                Name = $"{PluginBundleCatalog.SeedPlugin}-fanout-{Guid.NewGuid():N}",
                Description = "Integration test blueprint with fan out.",
                StartingBlueprintNodeId = seedNodeId,
                Tasks =
                [
                    CreateTask(
                        seedNodeId,
                        seedTaskVersion,
                        120,
                        200,
                        outgoingConnections:
                        [
                            CreateConnection(leftNodeId, "Success"),
                            CreateConnection(rightNodeId, "Success")
                        ]),
                    CreateTask(leftNodeId, leftTaskVersion, 470, 110),
                    CreateTask(rightNodeId, rightTaskVersion, 470, 290)
                ]
            },
            cancellationToken);

        return new FanOutScenario(
            createdVersion.BlueprintDefinitionId,
            createdVersion.BlueprintVersionId,
            seedNodeId,
            leftNodeId,
            rightNodeId);
    }

    public static Task<JoinScenario> CreateJoinScenarioAsync(
        DistributedAppTestEnvironment environment,
        CancellationToken cancellationToken) =>
        CreateJoinScenarioInternalAsync(
            environment,
            $"{PluginBundleCatalog.SeedPlugin}-join-{Guid.NewGuid():N}",
            PluginBundleCatalog.TransformPlugin,
            rightDesignTimeOverrides: CreateOverrides(
                ("NormalizedName", "RIGHT"),
                ("Score", 7)),
            joinDesignTimeOverrides: null,
            rightPropertyMappings:
            [
                ("Message", "RightText"),
                ("FinalScore", "CombinedScore")
            ],
            cancellationToken);

    public static Task<JoinScenario> CreateDesignTimeOverrideScenarioAsync(
        DistributedAppTestEnvironment environment,
        CancellationToken cancellationToken) =>
        CreateJoinScenarioInternalAsync(
            environment,
            $"{PluginBundleCatalog.SeedPlugin}-design-time-{Guid.NewGuid():N}",
            PluginBundleCatalog.TransformPlugin,
            rightDesignTimeOverrides: CreateOverrides(
                ("NormalizedName", "RIGHT"),
                ("Score", 7)),
            joinDesignTimeOverrides: CreateOverrides(
                ("LeftText", "DESIGN-LEFT"),
                ("Mode", "design-mode")),
            rightPropertyMappings:
            [
                ("Message", "RightText"),
                ("FinalScore", "CombinedScore")
            ],
            cancellationToken);

    public static Task<JoinScenario> CreateRuntimeOverrideScenarioAsync(
        DistributedAppTestEnvironment environment,
        CancellationToken cancellationToken) =>
        CreateJoinScenarioInternalAsync(
            environment,
            $"{PluginBundleCatalog.SeedPlugin}-runtime-{Guid.NewGuid():N}",
            PluginBundleCatalog.TransformPlugin,
            rightDesignTimeOverrides: CreateOverrides(
                ("NormalizedName", "RIGHT"),
                ("Score", 7)),
            joinDesignTimeOverrides: null,
            rightPropertyMappings:
            [
                ("Message", "RightText"),
                ("FinalScore", "CombinedScore")
            ],
            cancellationToken);

    public static Task<JoinScenario> CreateOverridePrecedenceScenarioAsync(
        DistributedAppTestEnvironment environment,
        CancellationToken cancellationToken) =>
        CreateJoinScenarioInternalAsync(
            environment,
            $"{PluginBundleCatalog.SeedPlugin}-precedence-{Guid.NewGuid():N}",
            PluginBundleCatalog.TransformPlugin,
            rightDesignTimeOverrides: CreateOverrides(
                ("NormalizedName", "RIGHT"),
                ("Score", 7)),
            joinDesignTimeOverrides: CreateOverrides(
                ("LeftText", "DESIGN-LEFT"),
                ("Mode", "design-mode")),
            rightPropertyMappings:
            [
                ("Message", "RightText"),
                ("FinalScore", "CombinedScore")
            ],
            cancellationToken);

    public static Task<JoinScenario> CreateFailedBranchJoinScenarioAsync(
        DistributedAppTestEnvironment environment,
        CancellationToken cancellationToken) =>
        CreateJoinScenarioInternalAsync(
            environment,
            $"{PluginBundleCatalog.SeedPlugin}-failed-join-{Guid.NewGuid():N}",
            PluginBundleCatalog.FailurePlugin,
            rightDesignTimeOverrides: CreateOverrides(("ShouldFail", true)),
            joinDesignTimeOverrides: null,
            rightPropertyMappings:
            [
                ("Outcome", "RightText")
            ],
            cancellationToken);

    public static BlueprintProcessInputModel CreateProcessInput<TInitialTaskInput>(
        TInitialTaskInput initialTaskInput,
        IReadOnlyDictionary<Guid, Dictionary<string, JsonElement>>? nodeOverrides = null)
    {
        var serializedNodeOverrides = nodeOverrides is null
            ? new Dictionary<Guid, Dictionary<string, JsonElement>>()
            : nodeOverrides.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.ToDictionary(
                    property => property.Key,
                    property => property.Value.Clone(),
                    StringComparer.Ordinal));

        return new BlueprintProcessInputModel
        {
            InitialTaskInput = ToJsonDictionary(initialTaskInput),
            NodeOverrides = serializedNodeOverrides
        };
    }

    private static async Task<JoinScenario> CreateJoinScenarioInternalAsync(
        DistributedAppTestEnvironment environment,
        string blueprintName,
        string rightPluginName,
        IReadOnlyDictionary<string, JsonElement>? rightDesignTimeOverrides,
        IReadOnlyDictionary<string, JsonElement>? joinDesignTimeOverrides,
        IReadOnlyList<(string SourceProperty, string TargetProperty)> rightPropertyMappings,
        CancellationToken cancellationToken)
    {
        var tasks = await environment.GetTasksAsync(cancellationToken);
        var seedTaskVersion = GetTaskVersion(tasks, PluginBundleCatalog.SeedPlugin, environment.Settings.RunVersion);
        var leftTaskVersion = GetTaskVersion(tasks, PluginBundleCatalog.TransformPlugin, environment.Settings.RunVersion);
        var rightTaskVersion = GetTaskVersion(tasks, rightPluginName, environment.Settings.RunVersion);
        var joinTaskVersion = GetTaskVersion(tasks, PluginBundleCatalog.JoinProbePlugin, environment.Settings.RunVersion);

        var seedNodeId = Guid.NewGuid();
        var leftNodeId = Guid.NewGuid();
        var rightNodeId = Guid.NewGuid();
        var joinNodeId = Guid.NewGuid();

        var createdVersion = await CreateBlueprintAsync(
            environment,
            new BlueprintInputModel
            {
                Name = blueprintName,
                Description = "Integration test blueprint with join and override coverage.",
                StartingBlueprintNodeId = seedNodeId,
                Tasks =
                [
                    CreateTask(
                        seedNodeId,
                        seedTaskVersion,
                        120,
                        200,
                        outgoingConnections:
                        [
                            CreateConnection(leftNodeId, "Success"),
                            CreateConnection(rightNodeId, "Success")
                        ]),
                    CreateTask(
                        leftNodeId,
                        leftTaskVersion,
                        440,
                        110,
                        outgoingConnections:
                        [
                            CreateConnection(
                                joinNodeId,
                                "Success",
                                ("Message", "LeftText"))
                        ]),
                    CreateTask(
                        rightNodeId,
                        rightTaskVersion,
                        440,
                        290,
                        designTimeOverrides: rightDesignTimeOverrides,
                        outgoingConnections:
                        [
                            CreateConnection(joinNodeId, "Success", rightPropertyMappings.ToArray())
                        ]),
                    CreateTask(
                        joinNodeId,
                        joinTaskVersion,
                        780,
                        200,
                        designTimeOverrides: joinDesignTimeOverrides)
                ]
            },
            cancellationToken);

        return new JoinScenario(
            createdVersion.BlueprintDefinitionId,
            createdVersion.BlueprintVersionId,
            seedNodeId,
            leftNodeId,
            rightNodeId,
            joinNodeId);
    }

    private static async Task<BlueprintScenario> CreateVersionedBlueprintAsync(
        DistributedAppTestEnvironment environment,
        string blueprintName,
        string[] pluginNames,
        IReadOnlyDictionary<string, JsonElement>? middleDesignTimeOverrides,
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
                CreateTask(
                    firstTaskIdV1,
                    firstTaskVersion,
                    100,
                    120,
                    outgoingConnections:
                    [
                        CreateConnection(middleTaskIdV1, "Success")
                    ]),
                CreateTask(
                    middleTaskIdV1,
                    middleTaskVersion,
                    420,
                    120,
                    designTimeOverrides: middleDesignTimeOverrides,
                    outgoingConnections:
                    [
                        CreateConnection(finalTaskIdV1, "Success")
                    ]),
                CreateTask(finalTaskIdV1, finalTaskVersion, 740, 120)
            ]
        };

        var createdBlueprint = await PostBlueprintAsync(environment, initialBlueprint, cancellationToken);

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
                CreateTask(
                    firstTaskIdV2,
                    firstTaskVersion,
                    120,
                    160,
                    outgoingConnections:
                    [
                        CreateConnection(middleTaskIdV2, "Success")
                    ]),
                CreateTask(
                    middleTaskIdV2,
                    middleTaskVersion,
                    460,
                    160,
                    designTimeOverrides: middleDesignTimeOverrides,
                    outgoingConnections:
                    [
                        CreateConnection(finalTaskIdV2, "Success")
                    ]),
                CreateTask(finalTaskIdV2, finalTaskVersion, 780, 160)
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

    private static async Task<(int BlueprintDefinitionId, int BlueprintVersionId)> CreateBlueprintAsync(
        DistributedAppTestEnvironment environment,
        BlueprintInputModel blueprint,
        CancellationToken cancellationToken)
    {
        var createdBlueprint = await PostBlueprintAsync(environment, blueprint, cancellationToken);
        var createdVersion = createdBlueprint.Versions
            .OrderByDescending(version => version.Version)
            .FirstOrDefault();

        createdVersion.ShouldNotBeNull();
        return (createdBlueprint.Id, createdVersion.BlueprintVersionId);
    }

    private static async Task<BlueprintViewModel> PostBlueprintAsync(
        DistributedAppTestEnvironment environment,
        BlueprintInputModel blueprint,
        CancellationToken cancellationToken)
    {
        var createResponse = await environment.ApiClient.PostAsJsonAsync(
            "/api/blueprints",
            blueprint,
            cancellationToken);

        createResponse.IsSuccessStatusCode.ShouldBeTrue(await createResponse.Content.ReadAsStringAsync(cancellationToken));

        var createdBlueprint = await createResponse.Content.ReadFromJsonAsync<BlueprintViewModel>(cancellationToken: cancellationToken);
        createdBlueprint.ShouldNotBeNull();
        return createdBlueprint;
    }

    private static BlueprintNodeInputModel CreateTask(
        Guid instanceId,
        TaskVersionReference taskVersion,
        int x,
        int y,
        IReadOnlyDictionary<string, JsonElement>? designTimeOverrides = null,
        IReadOnlyCollection<BlueprintConnectionInputModel>? outgoingConnections = null)
    {
        var (allowedMajor, allowedMinor) = GetVersionLane(taskVersion.Version);
        var task = new BlueprintNodeInputModel
        {
            InstanceId = instanceId,
            TaskDefinitionId = taskVersion.TaskDefinitionId,
            AllowedMajor = allowedMajor,
            AllowedMinor = allowedMinor,
            Position = new CoordinatesViewModel { X = x, Y = y },
            DesignTimeInputOverrides = CloneOverrides(designTimeOverrides)
        };

        if (outgoingConnections is not null)
        {
            task.OutgoingConnections.AddRange(outgoingConnections);
        }

        return task;
    }

    private static BlueprintConnectionInputModel CreateConnection(
        Guid connectedBlueprintNodeId,
        string channelName,
        params (string SourceProperty, string TargetProperty)[] propertyMappings) =>
        new()
        {
            ConnectedBlueprintNodeId = connectedBlueprintNodeId,
            ChannelName = channelName,
            Anchor = 0,
            Side = "right",
            Position = 0,
            PropertyMappings =
            [
                .. propertyMappings.Select(mapping => new BlueprintPropertyMappingInputModel
                {
                    SourceProperty = mapping.SourceProperty,
                    TargetProperty = mapping.TargetProperty
                })
            ]
        };

    private static TaskVersionReference GetTaskVersion(
        IEnumerable<TaskDefinitionViewModel> tasks,
        string pluginName,
        string version)
    {
        var taskDefinition = tasks.Single(task => task.Name == pluginName);
        var taskVersion = taskDefinition.Versions
            .Single(versionViewModel => VersionsMatch(versionViewModel.Version, version));

        return new TaskVersionReference(taskDefinition.Id, taskVersion.Version);
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

    private static Dictionary<string, JsonElement> ToJsonDictionary<T>(T value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
            JsonSerializerOptions.Web)
        ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> CreateOverrides(
        params (string Key, object? Value)[] entries) =>
        entries.ToDictionary(
            entry => entry.Key,
            entry => JsonSerializer.SerializeToElement(entry.Value, JsonSerializerOptions.Web),
            StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> CloneOverrides(
        IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        if (overrides is null)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        return overrides.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Clone(),
            StringComparer.Ordinal);
    }
}

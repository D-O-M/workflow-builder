using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Tests.TestSupport;
using Backend.Database.Relational.Entities;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Shouldly;

namespace Backend.API.Tests;

public sealed class BlueprintRepositoryTests
{
    [Test]
    public async Task CreateBlueprint_persists_definition_display_name()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var taskDefinition = await SeedTaskDefinitionAsync(database.Context, "Lookup", new Version(1, 0, 0));
        var repository = new BlueprintRepository(database.Context);
        var firstNodeId = Guid.NewGuid();

        var created = await repository.CreateBlueprint(
            CreateBlueprintInput("Customer Intake", taskDefinition.Id, firstNodeId),
            CancellationToken.None);

        created.Name.ShouldBe("Customer Intake");
        var storedDefinition = await database.Context.BlueprintDefinitions.FindAsync(created.Id);
        storedDefinition.ShouldNotBeNull();
        storedDefinition.DisplayName.ShouldBe("Customer Intake");
    }

    [Test]
    public async Task CreateVersion_keeps_existing_definition_display_name()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var taskDefinition = await SeedTaskDefinitionAsync(database.Context, "Lookup", new Version(1, 0, 0));
        var repository = new BlueprintRepository(database.Context);
        var firstNodeId = Guid.NewGuid();

        var created = await repository.CreateBlueprint(
            CreateBlueprintInput("Immutable Blueprint", taskDefinition.Id, firstNodeId),
            CancellationToken.None);

        var nextNodeId = Guid.NewGuid();
        var createdVersion = await repository.CreateVersion(
            CreateBlueprintVersion(created.Id, taskDefinition.Id, nextNodeId),
            CancellationToken.None);

        createdVersion.BlueprintDefinitionId.ShouldBe(created.Id);
        var reloaded = await repository.GetBlueprintById(created.Id, CancellationToken.None);
        reloaded.Name.ShouldBe("Immutable Blueprint");
        reloaded.Versions.Count.ShouldBe(2);
        reloaded.Versions[0].BlueprintVersionId.ShouldBe(createdVersion.BlueprintVersionId);
        reloaded.Versions[0].Version.ShouldBe(2);
    }

    [Test]
    public async Task GetFullBlueprintById_compiles_bindings_and_design_time_overrides()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sourceDefinition = await SeedTaskDefinitionWithChannelsAsync(
            database.Context,
            "Lookup",
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["requestId"] = WorkflowPropertyKind.Guid,
            },
            ("TownResult", new Dictionary<string, WorkflowPropertyKind>
            {
                ["town"] = WorkflowPropertyKind.String,
            }));
        var holidayDefinition = await SeedTaskDefinitionWithChannelsAsync(
            database.Context,
            "Holiday",
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["requestId"] = WorkflowPropertyKind.Guid,
            },
            ("HolidayResult", new Dictionary<string, WorkflowPropertyKind>
            {
                ["holiday"] = WorkflowPropertyKind.String,
            }));
        var targetDefinition = await SeedTaskDefinitionWithChannelsAsync(
            database.Context,
            "Recommendation",
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["mappedTown"] = WorkflowPropertyKind.String,
                ["holiday"] = WorkflowPropertyKind.String,
                ["mode"] = WorkflowPropertyKind.String,
            });

        var repository = new BlueprintRepository(database.Context);
        var sourceNodeId = Guid.NewGuid();
        var holidayNodeId = Guid.NewGuid();
        var targetNodeId = Guid.NewGuid();

        var created = await repository.CreateBlueprint(
            new BlueprintInputModel
            {
                Name = "Joined Blueprint",
                StartingBlueprintNodeId = sourceNodeId,
                Tasks =
                [
                    new BlueprintNodeInputModel
                    {
                        InstanceId = sourceNodeId,
                        TaskDefinitionId = sourceDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 100, Y = 80 },
                        OutgoingConnections =
                        [
                            new BlueprintConnectionInputModel
                            {
                                ConnectedBlueprintNodeId = targetNodeId,
                                ChannelName = "TownResult",
                                Anchor = 0,
                                Side = "right",
                                Position = 0,
                                PropertyMappings =
                                [
                                    new BlueprintPropertyMappingInputModel
                                    {
                                        SourceProperty = "town",
                                        TargetProperty = "mappedTown",
                                    }
                                ]
                            }
                        ]
                    },
                    new BlueprintNodeInputModel
                    {
                        InstanceId = holidayNodeId,
                        TaskDefinitionId = holidayDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 360, Y = 80 },
                        OutgoingConnections =
                        [
                            new BlueprintConnectionInputModel
                            {
                                ConnectedBlueprintNodeId = targetNodeId,
                                ChannelName = "HolidayResult",
                                Anchor = 0,
                                Side = "right",
                                Position = 0,
                            }
                        ]
                    },
                    new BlueprintNodeInputModel
                    {
                        InstanceId = targetNodeId,
                        TaskDefinitionId = targetDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 620, Y = 80 },
                        DesignTimeInputOverrides = new Dictionary<string, JsonElement>
                        {
                            ["mode"] = JsonSerializer.SerializeToElement("design-time")
                        }
                    }
                ]
            },
            CancellationToken.None);

        var executable = await repository.GetFullBlueprintById(
            created.Versions[0].BlueprintVersionId,
            CancellationToken.None);

        var sourceNode = executable.NodesById[sourceNodeId];
        var holidayNode = executable.NodesById[holidayNodeId];
        var targetNode = executable.NodesById[targetNodeId];

        sourceNode.OutputChannels["TownResult"].OutboundBindings.Count.ShouldBe(1);
        holidayNode.OutputChannels["HolidayResult"].OutboundBindings.Count.ShouldBe(1);
        targetNode.InboundBindings.Count.ShouldBe(2);
        targetNode.DesignTimeInputOverrides["mode"].GetString().ShouldBe("design-time");
        targetNode.InboundBindings.ShouldContain(binding =>
            binding.PropertyMappings.Any(mapping =>
                mapping.SourceProperty == "town" &&
                mapping.TargetProperty == "mappedTown"));
        targetNode.InboundBindings.ShouldContain(binding =>
            binding.PropertyMappings.Any(mapping =>
                mapping.SourceProperty == "holiday" &&
                mapping.TargetProperty == "holiday"));
    }

    private static async Task<TaskDefinition> SeedTaskDefinitionAsync(
        Backend.Database.Relational.BlueprintDbContext context,
        string name,
        Version version,
        params string[] outputChannels)
    {
        var taskDefinition = new TaskDefinition
        {
            Name = name,
            Versions =
            [
                new TaskVersion
                {
                    Version = version,
                    Path = $"/plugins/{name.ToLowerInvariant()}",
                    State = TaskVersionState.Active,
                    Channels = outputChannels
                        .Select(channelName => (Channel)new OutputChannelDefinition { Name = channelName })
                        .ToList()
                }
            ]
        };

        context.TaskDefinitions.Add(taskDefinition);
        await context.SaveChangesAsync();
        return taskDefinition;
    }

    private static async Task<TaskDefinition> SeedTaskDefinitionWithChannelsAsync(
        Backend.Database.Relational.BlueprintDbContext context,
        string name,
        Dictionary<string, WorkflowPropertyKind> inputProperties,
        params (string ChannelName, Dictionary<string, WorkflowPropertyKind> Properties)[] outputChannels)
    {
        var taskDefinition = new TaskDefinition
        {
            Name = name,
            Versions =
            [
                new TaskVersion
                {
                    Version = new Version(1, 0, 0),
                    Path = $"/plugins/{name.ToLowerInvariant()}",
                    State = TaskVersionState.Active,
                    Channels =
                    [
                        new InputChannelDefinition
                        {
                            Name = "Input",
                            Properties = inputProperties
                                .Select(property => new ChannelProperty
                                {
                                    Name = property.Key,
                                    Kind = property.Value,
                                    IsRequired = true,
                                })
                                .ToList()
                        },
                        .. outputChannels.Select(channel => new OutputChannelDefinition
                        {
                            Name = channel.ChannelName,
                            Properties = channel.Properties
                                .Select(property => new ChannelProperty
                                {
                                    Name = property.Key,
                                    Kind = property.Value,
                                    IsRequired = true,
                                })
                                .ToList()
                        })
                    ]
                }
            ]
        };

        context.TaskDefinitions.Add(taskDefinition);
        await context.SaveChangesAsync();
        return taskDefinition;
    }

    private static BlueprintInputModel CreateBlueprintInput(string name, int taskDefinitionId, Guid nodeId)
    {
        var version = new Version(1, 0, 0);
        return new BlueprintInputModel
        {
            Name = name,
            StartingBlueprintNodeId = nodeId,
            Tasks =
            [
                CreateBlueprintNode(nodeId, taskDefinitionId, version)
            ]
        };
    }

    private static BlueprintVersionInputModel CreateBlueprintVersion(int definitionId, int taskDefinitionId, Guid nodeId)
    {
        var version = new Version(1, 0, 0);
        var (allowedMajor, allowedMinor) = GetLane(version);

        return new BlueprintVersionInputModel
        {
            BlueprintDefinitionId = definitionId,
            StartingBlueprintNodeId = nodeId,
            Tasks =
            [
                new BlueprintNodeInputModel
                {
                    InstanceId = nodeId,
                    TaskDefinitionId = taskDefinitionId,
                    AllowedMajor = allowedMajor,
                    AllowedMinor = allowedMinor,
                    Position = new CoordinatesViewModel
                    {
                        X = 180,
                        Y = 120,
                    }
                }
            ]
        };
    }

    private static BlueprintNodeInputModel CreateBlueprintNode(Guid nodeId, int taskDefinitionId, Version version)
    {
        var (allowedMajor, allowedMinor) = GetLane(version);
        return new BlueprintNodeInputModel
        {
            InstanceId = nodeId,
            TaskDefinitionId = taskDefinitionId,
            AllowedMajor = allowedMajor,
            AllowedMinor = allowedMinor,
            Position = new CoordinatesViewModel
            {
                X = 180,
                Y = 120,
            }
        };
    }

    private static (int AllowedMajor, int AllowedMinor) GetLane(Version version) => (version.Major, version.Minor);
}

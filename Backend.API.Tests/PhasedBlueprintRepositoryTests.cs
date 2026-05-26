using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Tests.TestSupport;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Models.InputModels;
using Backend.Repositories;
using Shouldly;

namespace Backend.API.Tests;

public sealed class PhasedBlueprintRepositoryTests
{
    [Test]
    public async Task CreateAsync_persists_adjacent_phases_with_handoff_and_correlation()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sourceBlueprint = await SeedSingleNodeBlueprintAsync(
            database.Context,
            "Service Request",
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["requestId"] = WorkflowPropertyKind.Guid,
            },
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["deviceId"] = WorkflowPropertyKind.Guid,
            });
        var targetBlueprint = await SeedSingleNodeBlueprintAsync(
            database.Context,
            "Alert Response",
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["deviceId"] = WorkflowPropertyKind.Guid,
                ["alertCode"] = WorkflowPropertyKind.String,
            },
            new Dictionary<string, WorkflowPropertyKind>());
        var firstPhaseId = Guid.NewGuid();
        var secondPhaseId = Guid.NewGuid();

        var repository = new PhasedBlueprintRepository(database.Context);
        var created = await repository.CreateAsync(
            new PhasedBlueprintInputModel
            {
                Name = "DCC Service Request",
                Phases =
                [
                    CreatePhase(firstPhaseId, 0, "Send request", sourceBlueprint),
                    CreatePhase(secondPhaseId, 1, "Receive alert", targetBlueprint),
                ],
                Transitions =
                [
                    new PhasedBlueprintTransitionInputModel
                    {
                        TransitionId = Guid.NewGuid(),
                        SourcePhaseId = firstPhaseId,
                        DestinationPhaseId = secondPhaseId,
                        HandoffMappings =
                        [
                            new PhasedBlueprintHandoffMappingInputModel
                            {
                                SourceKind = "Output",
                                SourceBlueprintNodeId = sourceBlueprint.NodeId,
                                SourceChannelName = "Result",
                                SourceProperty = "deviceId",
                                BridgePosition = 0.42d,
                                TargetBlueprintNodeId = targetBlueprint.NodeId,
                                TargetProperty = "deviceId",
                            }
                        ],
                        CorrelationMappings =
                        [
                            new PhasedBlueprintCorrelationMappingInputModel
                            {
                                SourceResultProperty = "deviceId",
                                TriggerProperty = "deviceId",
                            }
                        ],
                    }
                ],
            },
            CancellationToken.None);

        created.Name.ShouldBe("DCC Service Request");
        created.Versions.Count.ShouldBe(1);
        created.Versions[0].Phases.Select(phase => phase.Name).ShouldBe(["Send request", "Receive alert"]);

        var storedVersion = await repository.GetVersionGraphAsync(
            created.Versions[0].PhasedBlueprintVersionId,
            CancellationToken.None);
        var transition = storedVersion.Transitions.Single();
        transition.HandoffMappings.Single().TargetBlueprintNodeId.ShouldBe(targetBlueprint.NodeId);
        transition.HandoffMappings.Single().TargetProperty.ShouldBe("deviceId");
        transition.HandoffMappings.Single().BridgePosition.ShouldBe(0.42d);
        transition.CorrelationMappings.Single().TriggerProperty.ShouldBe("deviceId");
    }

    [Test]
    public async Task CreateAsync_allows_handoff_to_non_start_destination_node()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sourceBlueprint = await SeedSingleNodeBlueprintAsync(
            database.Context,
            "Any Node Source",
            new Dictionary<string, WorkflowPropertyKind>(),
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["deviceId"] = WorkflowPropertyKind.Guid,
            });
        var targetBlueprint = await SeedTwoNodeBlueprintAsync(
            database.Context,
            "Any Node Target",
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["deviceId"] = WorkflowPropertyKind.Guid,
            });
        var firstPhaseId = Guid.NewGuid();
        var secondPhaseId = Guid.NewGuid();

        var repository = new PhasedBlueprintRepository(database.Context);
        var created = await repository.CreateAsync(
            new PhasedBlueprintInputModel
            {
                Name = "Any Node Mapping",
                Phases =
                [
                    CreatePhase(firstPhaseId, 0, "Source", sourceBlueprint),
                    CreatePhase(secondPhaseId, 1, "Target", targetBlueprint),
                ],
                Transitions =
                [
                    new PhasedBlueprintTransitionInputModel
                    {
                        TransitionId = Guid.NewGuid(),
                        SourcePhaseId = firstPhaseId,
                        DestinationPhaseId = secondPhaseId,
                        HandoffMappings =
                        [
                            new PhasedBlueprintHandoffMappingInputModel
                            {
                                SourceKind = "Output",
                                SourceBlueprintNodeId = sourceBlueprint.NodeId,
                                SourceChannelName = "Result",
                                SourceProperty = "deviceId",
                                TargetBlueprintNodeId = targetBlueprint.SecondaryNodeId!.Value,
                                TargetProperty = "deviceId",
                            }
                        ],
                    }
                ],
            },
            CancellationToken.None);

        var storedVersion = await repository.GetVersionGraphAsync(
            created.Versions[0].PhasedBlueprintVersionId,
            CancellationToken.None);

        storedVersion.Transitions.Single().HandoffMappings.Single().TargetBlueprintNodeId
            .ShouldBe(targetBlueprint.SecondaryNodeId!.Value);
    }

    [Test]
    public async Task CreateAsync_rejects_unknown_handoff_source_kinds()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sourceBlueprint = await SeedSingleNodeBlueprintAsync(
            database.Context,
            "Counter Source",
            new Dictionary<string, WorkflowPropertyKind>(),
            new Dictionary<string, WorkflowPropertyKind>());
        var targetBlueprint = await SeedSingleNodeBlueprintAsync(
            database.Context,
            "Counter Target",
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["attemptCount"] = WorkflowPropertyKind.String,
            },
            new Dictionary<string, WorkflowPropertyKind>());
        var firstPhaseId = Guid.NewGuid();
        var secondPhaseId = Guid.NewGuid();

        var repository = new PhasedBlueprintRepository(database.Context);

        var exception = await Should.ThrowAsync<InvalidDataException>(() => repository.CreateAsync(
            new PhasedBlueprintInputModel
            {
                Name = "Rejected Handoff Source",
                Phases =
                [
                    CreatePhase(firstPhaseId, 0, "Source", sourceBlueprint),
                    CreatePhase(secondPhaseId, 1, "Target", targetBlueprint),
                ],
                Transitions =
                [
                    new PhasedBlueprintTransitionInputModel
                    {
                        TransitionId = Guid.NewGuid(),
                        SourcePhaseId = firstPhaseId,
                        DestinationPhaseId = secondPhaseId,
                        HandoffMappings =
                        [
                            new PhasedBlueprintHandoffMappingInputModel
                            {
                                SourceKind = "ProcessContext",
                                SourceProperty = "attemptCount",
                                TargetBlueprintNodeId = targetBlueprint.NodeId,
                                TargetProperty = "attemptCount",
                            }
                        ],
                    }
                ],
            },
            CancellationToken.None));

        exception.Message.ShouldContain("Phase handoff source kind 'ProcessContext' is not supported");
    }

    [Test]
    public async Task CreateAsync_requires_correlation_to_reference_handoff_result()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sourceBlueprint = await SeedSingleNodeBlueprintAsync(
            database.Context,
            "Correlation Source",
            new Dictionary<string, WorkflowPropertyKind>(),
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["deviceId"] = WorkflowPropertyKind.Guid,
            });
        var targetBlueprint = await SeedSingleNodeBlueprintAsync(
            database.Context,
            "Correlation Target",
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["deviceId"] = WorkflowPropertyKind.Guid,
            },
            new Dictionary<string, WorkflowPropertyKind>());
        var firstPhaseId = Guid.NewGuid();
        var secondPhaseId = Guid.NewGuid();

        var repository = new PhasedBlueprintRepository(database.Context);

        var exception = await Should.ThrowAsync<InvalidDataException>(() => repository.CreateAsync(
            new PhasedBlueprintInputModel
            {
                Name = "Rejected Correlation",
                Phases =
                [
                    CreatePhase(firstPhaseId, 0, "Source", sourceBlueprint),
                    CreatePhase(secondPhaseId, 1, "Target", targetBlueprint),
                ],
                Transitions =
                [
                    new PhasedBlueprintTransitionInputModel
                    {
                        TransitionId = Guid.NewGuid(),
                        SourcePhaseId = firstPhaseId,
                        DestinationPhaseId = secondPhaseId,
                        HandoffMappings =
                        [
                            new PhasedBlueprintHandoffMappingInputModel
                            {
                                SourceKind = "Output",
                                SourceBlueprintNodeId = sourceBlueprint.NodeId,
                                SourceChannelName = "Result",
                                SourceProperty = "deviceId",
                                TargetBlueprintNodeId = targetBlueprint.NodeId,
                                TargetProperty = "deviceId",
                            }
                        ],
                        CorrelationMappings =
                        [
                            new PhasedBlueprintCorrelationMappingInputModel
                            {
                                SourceResultProperty = "missing",
                                TriggerProperty = "deviceId",
                            }
                        ],
                    }
                ],
            },
            CancellationToken.None));

        exception.Message.ShouldContain("Correlation mappings must reference a handoff result property");
    }

    [Test]
    public async Task CreateAsync_rejects_duplicate_correlation_trigger_properties()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sourceBlueprint = await SeedSingleNodeBlueprintAsync(
            database.Context,
            "Duplicate Correlation Source",
            new Dictionary<string, WorkflowPropertyKind>(),
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["buildId"] = WorkflowPropertyKind.String,
            });
        var targetBlueprint = await SeedSingleNodeBlueprintAsync(
            database.Context,
            "Duplicate Correlation Target",
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["buildId"] = WorkflowPropertyKind.String,
            },
            new Dictionary<string, WorkflowPropertyKind>());
        var firstPhaseId = Guid.NewGuid();
        var secondPhaseId = Guid.NewGuid();

        var repository = new PhasedBlueprintRepository(database.Context);

        var exception = await Should.ThrowAsync<InvalidDataException>(() => repository.CreateAsync(
            new PhasedBlueprintInputModel
            {
                Name = "Rejected Duplicate Correlation",
                Phases =
                [
                    CreatePhase(firstPhaseId, 0, "Source", sourceBlueprint),
                    CreatePhase(secondPhaseId, 1, "Target", targetBlueprint),
                ],
                Transitions =
                [
                    new PhasedBlueprintTransitionInputModel
                    {
                        TransitionId = Guid.NewGuid(),
                        SourcePhaseId = firstPhaseId,
                        DestinationPhaseId = secondPhaseId,
                        HandoffMappings =
                        [
                            new PhasedBlueprintHandoffMappingInputModel
                            {
                                SourceKind = "Output",
                                SourceBlueprintNodeId = sourceBlueprint.NodeId,
                                SourceChannelName = "Result",
                                SourceProperty = "buildId",
                                TargetBlueprintNodeId = targetBlueprint.NodeId,
                                TargetProperty = "buildId",
                            }
                        ],
                        CorrelationMappings =
                        [
                            new PhasedBlueprintCorrelationMappingInputModel
                            {
                                SourceResultProperty = "buildId",
                                TriggerProperty = "BuildId",
                            },
                            new PhasedBlueprintCorrelationMappingInputModel
                            {
                                SourceResultProperty = "buildId",
                                TriggerProperty = "BuildId",
                            }
                        ],
                    }
                ],
            },
            CancellationToken.None));

        exception.Message.ShouldContain("Correlation trigger property names must be unique");
    }

    private static PhasedBlueprintPhaseInputModel CreatePhase(
        Guid phaseId,
        int phaseIndex,
        string name,
        BlueprintSeed seed) =>
        new()
        {
            PhaseId = phaseId,
            PhaseIndex = phaseIndex,
            Name = name,
            BlueprintDefinitionId = seed.DefinitionId,
            BlueprintVersionId = seed.VersionId,
            VersionPolicy = "Pinned",
        };

    private static async Task<BlueprintSeed> SeedSingleNodeBlueprintAsync(
        BlueprintDbContext context,
        string name,
        Dictionary<string, WorkflowPropertyKind> inputProperties,
        Dictionary<string, WorkflowPropertyKind> outputProperties)
    {
        var taskDefinition = new TaskDefinition
        {
            Name = name.Replace(" ", string.Empty),
            Versions =
            [
                new TaskVersion
                {
                    Version = new Version(1, 0, 0),
                    Path = $"/plugins/{name.Replace(" ", "-").ToLowerInvariant()}",
                    State = TaskVersionState.Active,
                    Channels = BuildChannels(inputProperties, outputProperties),
                }
            ],
        };
        var nodeId = Guid.NewGuid();
        var blueprintDefinition = new BlueprintDefinition
        {
            Key = name.Replace(" ", "-").ToLowerInvariant(),
            DisplayName = name,
        };
        var version = new BlueprintVersion
        {
            BlueprintDefinition = blueprintDefinition,
            Version = 1,
            CreatedAt = DateTimeOffset.Parse("2026-04-29T00:00:00Z"),
        };
        version.BlueprintNodes.Add(new BlueprintNode
        {
            Id = nodeId,
            BlueprintVersion = version,
            TaskDefinition = taskDefinition,
            AllowedMajor = 1,
            AllowedMinor = 0,
            PositionX = 100,
            PositionY = 100,
        });
        blueprintDefinition.Versions.Add(version);

        context.BlueprintDefinitions.Add(blueprintDefinition);
        await context.SaveChangesAsync();
        version.StartingBlueprintNodeId = nodeId;
        await context.SaveChangesAsync();

        return new BlueprintSeed(blueprintDefinition.Id, version.Id, nodeId);
    }

    private static async Task<BlueprintSeed> SeedTwoNodeBlueprintAsync(
        BlueprintDbContext context,
        string name,
        Dictionary<string, WorkflowPropertyKind> secondNodeInputProperties)
    {
        var startTaskDefinition = new TaskDefinition
        {
            Name = name.Replace(" ", string.Empty) + "Start",
            Versions =
            [
                new TaskVersion
                {
                    Version = new Version(1, 0, 0),
                    Path = $"/plugins/{name.Replace(" ", "-").ToLowerInvariant()}-start",
                    State = TaskVersionState.Active,
                    Channels = BuildChannels(new Dictionary<string, WorkflowPropertyKind>(), new Dictionary<string, WorkflowPropertyKind>()),
                }
            ],
        };
        var targetTaskDefinition = new TaskDefinition
        {
            Name = name.Replace(" ", string.Empty) + "Target",
            Versions =
            [
                new TaskVersion
                {
                    Version = new Version(1, 0, 0),
                    Path = $"/plugins/{name.Replace(" ", "-").ToLowerInvariant()}-target",
                    State = TaskVersionState.Active,
                    Channels = BuildChannels(secondNodeInputProperties, new Dictionary<string, WorkflowPropertyKind>()),
                }
            ],
        };
        var startNodeId = Guid.NewGuid();
        var targetNodeId = Guid.NewGuid();
        var blueprintDefinition = new BlueprintDefinition
        {
            Key = name.Replace(" ", "-").ToLowerInvariant(),
            DisplayName = name,
        };
        var version = new BlueprintVersion
        {
            BlueprintDefinition = blueprintDefinition,
            Version = 1,
            CreatedAt = DateTimeOffset.Parse("2026-04-29T00:00:00Z"),
        };
        version.BlueprintNodes.Add(new BlueprintNode
        {
            Id = startNodeId,
            BlueprintVersion = version,
            TaskDefinition = startTaskDefinition,
            AllowedMajor = 1,
            AllowedMinor = 0,
            PositionX = 100,
            PositionY = 100,
        });
        version.BlueprintNodes.Add(new BlueprintNode
        {
            Id = targetNodeId,
            BlueprintVersion = version,
            TaskDefinition = targetTaskDefinition,
            AllowedMajor = 1,
            AllowedMinor = 0,
            PositionX = 340,
            PositionY = 100,
        });
        blueprintDefinition.Versions.Add(version);

        context.BlueprintDefinitions.Add(blueprintDefinition);
        await context.SaveChangesAsync();
        version.StartingBlueprintNodeId = startNodeId;
        await context.SaveChangesAsync();

        return new BlueprintSeed(blueprintDefinition.Id, version.Id, startNodeId, targetNodeId);
    }

    private static List<Channel> BuildChannels(
        Dictionary<string, WorkflowPropertyKind> inputProperties,
        Dictionary<string, WorkflowPropertyKind> outputProperties)
    {
        var channels = new List<Channel>
        {
            new InputChannelDefinition
            {
                Name = "Input",
                Properties = CreateProperties(inputProperties),
            },
            new OutputChannelDefinition
            {
                Name = "Result",
                Properties = CreateProperties(outputProperties),
            },
        };

        return channels;
    }

    private static List<ChannelProperty> CreateProperties(
        Dictionary<string, WorkflowPropertyKind> properties) =>
        properties
            .Select(property => new ChannelProperty
            {
                Name = property.Key,
                Kind = property.Value,
                IsRequired = true,
            })
            .ToList();

    private sealed record BlueprintSeed(int DefinitionId, int VersionId, Guid NodeId, Guid? SecondaryNodeId = null);
}

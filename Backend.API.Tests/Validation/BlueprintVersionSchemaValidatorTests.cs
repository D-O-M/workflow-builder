using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Tests.TestSupport;
using Backend.API.Validation.Blueprints;
using Backend.Database.Relational.Entities;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Shouldly;

namespace Backend.API.Tests.Validation;

public sealed class BlueprintVersionSchemaValidatorTests
{
    [Test]
    public async Task ValidateAsync_allows_multiple_incoming_edges_when_target_properties_are_distinct()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sourceDefinition = await SeedTaskDefinitionAsync(
            database.Context,
            "Source",
            inputProperties: new Dictionary<string, PrimitiveTypes> { ["requestId"] = PrimitiveTypes.Guid },
            ("Result", new Dictionary<string, PrimitiveTypes>
            {
                ["town"] = PrimitiveTypes.String,
                ["holiday"] = PrimitiveTypes.String,
            }));
        var targetDefinition = await SeedTaskDefinitionAsync(
            database.Context,
            "Target",
            inputProperties: new Dictionary<string, PrimitiveTypes>
            {
                ["mappedTown"] = PrimitiveTypes.String,
                ["holiday"] = PrimitiveTypes.String,
            });

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var model = Version(
            a,
            Task(a, sourceDefinition.Id, outgoing:
            [
                Connection(
                    c,
                    "Result",
                    ("town", "mappedTown"))
            ]),
            Task(b, sourceDefinition.Id, outgoing:
            [
                Connection(
                    c,
                    "Result",
                    ("holiday", "holiday"))
            ]),
            Task(c, targetDefinition.Id));

        var sut = new BlueprintVersionSchemaValidator(database.Context);
        var result = await sut.ValidateAsync(model, CancellationToken.None);

        result.IsValid.ShouldBeTrue();
        result.Errors.Count.ShouldBe(0);
    }

    [Test]
    public async Task ValidateAsync_rejects_duplicate_inbound_target_property_assignments()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sourceDefinition = await SeedTaskDefinitionAsync(
            database.Context,
            "Source",
            inputProperties: new Dictionary<string, PrimitiveTypes> { ["requestId"] = PrimitiveTypes.Guid },
            ("Result", new Dictionary<string, PrimitiveTypes>
            {
                ["town"] = PrimitiveTypes.String,
                ["holiday"] = PrimitiveTypes.String,
            }));
        var targetDefinition = await SeedTaskDefinitionAsync(
            database.Context,
            "Target",
            inputProperties: new Dictionary<string, PrimitiveTypes>
            {
                ["mappedTown"] = PrimitiveTypes.String,
                ["holiday"] = PrimitiveTypes.String,
            });

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var model = Version(
            a,
            Task(a, sourceDefinition.Id, outgoing:
            [
                Connection(
                    c,
                    "Result",
                    ("town", "mappedTown"))
            ]),
            Task(b, sourceDefinition.Id, outgoing:
            [
                Connection(
                    c,
                    "Result",
                    ("holiday", "mappedTown"))
            ]),
            Task(c, targetDefinition.Id));

        var sut = new BlueprintVersionSchemaValidator(database.Context);
        var result = await sut.ValidateAsync(model, CancellationToken.None);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == "graph.connection.mapping.duplicateTarget");
    }

    [Test]
    public async Task ValidateAsync_rejects_unknown_design_time_override_property()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var targetDefinition = await SeedTaskDefinitionAsync(
            database.Context,
            "Target",
            inputProperties: new Dictionary<string, PrimitiveTypes>
            {
                ["mappedTown"] = PrimitiveTypes.String,
            });

        var taskId = Guid.NewGuid();
        var model = Version(
            taskId,
            Task(
                taskId,
                targetDefinition.Id,
                designTimeOverrides: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["missing"] = JsonSerializer.SerializeToElement("nope")
                }));

        var sut = new BlueprintVersionSchemaValidator(database.Context);
        var result = await sut.ValidateAsync(model, CancellationToken.None);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == "task.override.targetMissing");
    }

    private static BlueprintVersionInputModel Version(Guid start, params BlueprintNodeInputModel[] tasks) =>
        new()
        {
            BlueprintDefinitionId = 1,
            StartingBlueprintNodeId = start,
            Tasks = tasks.ToList()
        };

    private static BlueprintNodeInputModel Task(
        Guid id,
        int taskDefinitionId,
        Dictionary<string, JsonElement>? designTimeOverrides = null,
        params BlueprintConnectionInputModel[] outgoing) =>
        new()
        {
            InstanceId = id,
            TaskDefinitionId = taskDefinitionId,
            AllowedMajor = 1,
            AllowedMinor = 0,
            Position = new CoordinatesViewModel { X = 120, Y = 80 },
            DesignTimeInputOverrides = designTimeOverrides ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            OutgoingConnections = outgoing.ToList()
        };

    private static BlueprintConnectionInputModel Connection(
        Guid destinationId,
        string channelName,
        params (string SourceProperty, string TargetProperty)[] mappings) =>
        new()
        {
            ConnectedBlueprintNodeId = destinationId,
            ChannelName = channelName,
            PropertyMappings = mappings
                .Select(mapping => new BlueprintPropertyMappingInputModel
                {
                    SourceProperty = mapping.SourceProperty,
                    TargetProperty = mapping.TargetProperty
                })
                .ToList()
        };

    private static async Task<TaskDefinition> SeedTaskDefinitionAsync(
        Backend.Database.Relational.BlueprintDbContext context,
        string name,
        IReadOnlyDictionary<string, PrimitiveTypes> inputProperties,
        params (string ChannelName, IReadOnlyDictionary<string, PrimitiveTypes> Properties)[] outputChannels)
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
                                    Type = property.Value
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
                                    Type = property.Value
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
}

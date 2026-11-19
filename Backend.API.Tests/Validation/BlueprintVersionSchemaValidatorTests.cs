using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Tests.TestSupport;
using Backend.API.Validation.Blueprints;
using Backend.Database.Relational.Entities;
using Backend.Models;
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
            inputProperties: new Dictionary<string, WorkflowPropertyKind> { ["requestId"] = WorkflowPropertyKind.Guid },
            ("Result", new Dictionary<string, WorkflowPropertyKind>
            {
                ["town"] = WorkflowPropertyKind.String,
                ["holiday"] = WorkflowPropertyKind.String,
            }));
        var targetDefinition = await SeedTaskDefinitionAsync(
            database.Context,
            "Target",
            inputProperties: new Dictionary<string, WorkflowPropertyKind>
            {
                ["mappedTown"] = WorkflowPropertyKind.String,
                ["holiday"] = WorkflowPropertyKind.String,
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
            inputProperties: new Dictionary<string, WorkflowPropertyKind> { ["requestId"] = WorkflowPropertyKind.Guid },
            ("Result", new Dictionary<string, WorkflowPropertyKind>
            {
                ["town"] = WorkflowPropertyKind.String,
                ["holiday"] = WorkflowPropertyKind.String,
            }));
        var targetDefinition = await SeedTaskDefinitionAsync(
            database.Context,
            "Target",
            inputProperties: new Dictionary<string, WorkflowPropertyKind>
            {
                ["mappedTown"] = WorkflowPropertyKind.String,
                ["holiday"] = WorkflowPropertyKind.String,
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
            inputProperties: new Dictionary<string, WorkflowPropertyKind>
            {
                ["mappedTown"] = WorkflowPropertyKind.String,
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

    [Test]
    public async Task Exclusive_routes_can_map_one_required_input_and_each_route_must_supply_it()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var schema = new Dictionary<string, WorkflowPropertyKind> { ["Amount"] = WorkflowPropertyKind.Decimal };
        var seed = await SeedTaskDefinitionAsync(database.Context, "Invoice", schema, ("Result", schema));
        var sink = await SeedTaskDefinitionAsync(database.Context, "Approve",
            new Dictionary<string, WorkflowPropertyKind> { ["Value"] = WorkflowPropertyKind.Decimal });
        var start = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var target = Guid.NewGuid();
        var rule = new Backend.Models.ConditionalRule
        {
            Kind = "comparison", Field = "Amount", Operator = "gt",
            Operands = [new() { Literal = JsonSerializer.SerializeToElement("9007199254740993.50") }]
        };
        var decision = new BlueprintNodeInputModel
        {
            InstanceId = branch, NodeKind = "ConditionalBranch", ConditionalRule = rule,
            OutgoingConnections = [Connection(target, "True", ("Amount", "Value")), Connection(target, "False", ("Amount", "Value"))]
        };
        var source = Task(start, seed.Id, outgoing: [Connection(branch, "Result")]);
        var version = Version(start, source, decision, Task(target, sink.Id));
        var validator = new BlueprintVersionSchemaValidator(database.Context);
        (await validator.ValidateAsync(version)).IsValid.ShouldBeTrue();

        decision.OutgoingConnections[1].PropertyMappings.Clear();
        (await validator.ValidateAsync(version)).Errors.ShouldContain(e => e.Code == "graph.route.requiredMissing");
        decision.OutgoingConnections[1].PropertyMappings.Add(new() { SourceProperty = "Amount", TargetProperty = "Value" });
        source.OutgoingConnections.Add(Connection(target, "Result", ("Amount", "Value")));
        (await validator.ValidateAsync(version)).Errors.ShouldContain(e => e.Code == "graph.connection.mapping.duplicateTarget");
        rule.Field = "Removed";
        (await validator.ValidateAsync(version)).Errors.ShouldContain(e => e.Path == "tasks[1].conditionalRule.field");
    }

    [Test]
    public async Task Importing_an_edited_condition_creates_an_immutable_version_and_reimport_matches_it()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var schema = new Dictionary<string, WorkflowPropertyKind> { ["Amount"] = WorkflowPropertyKind.Decimal };
        var seed = await SeedTaskDefinitionAsync(database.Context, "Invoice", schema, ("Result", schema));
        var start = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var repository = new Backend.Repositories.BlueprintRepository(database.Context);
        var service = new Backend.API.Services.BlueprintPortabilityService(database.Context, repository,
            new BlueprintVersionSchemaValidator(database.Context));
        var connection = new BlueprintConnectionInputModel
        {
            ConnectedBlueprintNodeId = branch, ChannelName = "Result", Side = "left", Anchor = 1
        };
        var created = await repository.CreateBlueprint(new()
        {
            Name = "Check amount", StartingBlueprintNodeId = start,
            Tasks = [Task(start, seed.Id, outgoing: [connection]), new()
            {
                InstanceId = branch, NodeKind = "ConditionalBranch", Position = new(),
                ConditionalRule = new() { Kind = "comparison", Field = "Amount", Operator = "gt",
                    Operands = [new() { Literal = JsonSerializer.SerializeToElement("100.5") }] }
            }]
        }, CancellationToken.None);

        var exported = await service.ExportLatestBlueprintAsync(created.Id, CancellationToken.None);
        var options = JsonSerializerOptions.Web;
        exported.SchemaVersion.ShouldBe(4);
        var copy = JsonSerializer.Deserialize<ExportedBlueprintInputModel>(JsonSerializer.Serialize(exported, options), options)!;
        copy.Tasks.Single(n => n.TaskKey == branch).ConditionalRule!.Negated = true;
        var imported = await service.ImportBlueprintAsync(copy, CancellationToken.None);
        imported.Status.ShouldBe(BlueprintImportStatus.CreatedVersion);
        var first = await repository.GetFullBlueprintById(created.Versions.Single().BlueprintVersionId, CancellationToken.None);
        var second = await repository.GetFullBlueprintById(imported.BlueprintVersionId, CancellationToken.None);
        first.NodesById[branch].ConditionalRule!.Negated.ShouldBeFalse();
        second.AllNodes.Single(n => n.ConditionalRule is not null).ConditionalRule!.Negated.ShouldBeTrue();
        (await service.ImportBlueprintAsync(copy, CancellationToken.None)).Status.ShouldBe(BlueprintImportStatus.MatchedExistingVersion);
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
        IReadOnlyDictionary<string, WorkflowPropertyKind> inputProperties,
        params (string ChannelName, IReadOnlyDictionary<string, WorkflowPropertyKind> Properties)[] outputChannels)
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
}

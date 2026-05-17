using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Engine;
using Backend.Models;
using Backend.Plugins.Communication;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class ActivationPlannerTests
{
    [Test]
    public void Single_inbound_binding_creates_ready_activation_intent()
    {
        var processId = Guid.NewGuid();
        var sourceNodeId = Guid.NewGuid();
        var destinationNodeId = Guid.NewGuid();
        var blueprint = CreateSingleInboundBlueprint(sourceNodeId, destinationNodeId, "source-to-destination");
        var sourceStepId = ProcessStepIdentity.Create(processId, sourceNodeId);
        var result = CreateResult(sourceStepId, "Out");

        var plan = PlanReadyActivations(blueprint, sourceNodeId, processId, sourceStepId, result);

        plan.JoinSourceObservations.Count.ShouldBe(0);
        var intent = plan.Intents.Single();
        intent.ProcessId.ShouldBe(processId);
        intent.ProcessStepId.ShouldBe(ProcessStepIdentity.Create(processId, destinationNodeId));
        intent.DestinationNode.Id.ShouldBe(destinationNodeId);
        intent.JoinStateId.ShouldBeNull();
        intent.InputHydrationPlan.InputChannelName.ShouldBe("DestinationInput");
        intent.InputHydrationPlan.Sources.Single().BindingId.ShouldBe("source-to-destination");
        intent.InputHydrationPlan.Sources.Single().SourceProcessStepId.ShouldBe(sourceStepId.ToString());
        intent.InputHydrationPlan.Sources.Single().SourceOutputChannelName.ShouldBe("Out");
    }

    [Test]
    public void Missing_output_data_creates_no_activation()
    {
        var processId = Guid.NewGuid();
        var sourceNodeId = Guid.NewGuid();
        var destinationNodeId = Guid.NewGuid();
        var blueprint = CreateSingleInboundBlueprint(sourceNodeId, destinationNodeId, "source-to-destination");
        var sourceStepId = ProcessStepIdentity.Create(processId, sourceNodeId);
        var result = CreateResult(sourceStepId, "DifferentOut");

        var plan = PlanReadyActivations(blueprint, sourceNodeId, processId, sourceStepId, result);

        plan.Intents.Count.ShouldBe(0);
        plan.JoinSourceObservations.Count.ShouldBe(0);
    }

    [Test]
    public void Multi_input_join_emits_source_observation_before_all_bindings_are_ready()
    {
        var processId = Guid.NewGuid();
        var leftNodeId = Guid.NewGuid();
        var rightNodeId = Guid.NewGuid();
        var joinNodeId = Guid.NewGuid();
        var blueprint = CreateJoinBlueprint(leftNodeId, rightNodeId, joinNodeId);
        var leftStepId = ProcessStepIdentity.Create(processId, leftNodeId);
        var result = CreateResult(leftStepId, "LeftOut");

        var plan = PlanReadyActivations(blueprint, leftNodeId, processId, leftStepId, result);

        plan.Intents.Count.ShouldBe(0);
        var observation = plan.JoinSourceObservations.Single();
        observation.JoinStateId.ShouldBe(joinNodeId);
        observation.DestinationBlueprintNodeId.ShouldBe(joinNodeId);
        observation.RequiredBindingIds.OrderBy(bindingId => bindingId, StringComparer.Ordinal)
            .ShouldBe(["left-to-join", "right-to-join"]);
        observation.Source.BindingId.ShouldBe("left-to-join");
    }

    [Test]
    public void Complete_join_snapshot_produces_joined_activation_with_ordered_sources()
    {
        var processId = Guid.NewGuid();
        var leftNodeId = Guid.NewGuid();
        var rightNodeId = Guid.NewGuid();
        var joinNodeId = Guid.NewGuid();
        var blueprint = CreateJoinBlueprint(leftNodeId, rightNodeId, joinNodeId);
        var leftStepId = ProcessStepIdentity.Create(processId, leftNodeId);
        var rightStepId = ProcessStepIdentity.Create(processId, rightNodeId);
        var leftPlan = PlanReadyActivations(
            blueprint,
            leftNodeId,
            processId,
            leftStepId,
            CreateResult(leftStepId, "LeftOut"));
        var snapshot = new JoinReadinessSnapshot(
            processId,
            blueprint.Id,
            joinNodeId,
            joinNodeId,
            ["left-to-join", "right-to-join"],
            new Dictionary<string, InputHydrationSource>(StringComparer.Ordinal)
            {
                ["left-to-join"] = leftPlan.JoinSourceObservations.Single().Source
            },
            StepEnqueued: false,
            EnqueuedProcessStepId: null);

        var plan = PlanReadyActivations(
            blueprint,
            rightNodeId,
            processId,
            rightStepId,
            CreateResult(rightStepId, "RightOut"),
            new Dictionary<Guid, JoinReadinessSnapshot>
            {
                [joinNodeId] = snapshot
            });

        var intent = plan.Intents.Single();
        intent.JoinStateId.ShouldBe(joinNodeId);
        intent.ProcessStepId.ShouldBe(ProcessStepIdentity.Create(processId, joinNodeId));
        intent.InputHydrationPlan.Sources.Select(source => source.BindingId)
            .ShouldBe(["left-to-join", "right-to-join"]);
    }

    [Test]
    public void Enqueued_join_snapshot_produces_no_duplicate_activation()
    {
        var processId = Guid.NewGuid();
        var leftNodeId = Guid.NewGuid();
        var rightNodeId = Guid.NewGuid();
        var joinNodeId = Guid.NewGuid();
        var blueprint = CreateJoinBlueprint(leftNodeId, rightNodeId, joinNodeId);
        var rightStepId = ProcessStepIdentity.Create(processId, rightNodeId);
        var snapshot = new JoinReadinessSnapshot(
            processId,
            blueprint.Id,
            joinNodeId,
            joinNodeId,
            ["left-to-join", "right-to-join"],
            new Dictionary<string, InputHydrationSource>(StringComparer.Ordinal),
            StepEnqueued: true,
            EnqueuedProcessStepId: ProcessStepIdentity.Create(processId, joinNodeId));

        var plan = PlanReadyActivations(
            blueprint,
            rightNodeId,
            processId,
            rightStepId,
            CreateResult(rightStepId, "RightOut"),
            new Dictionary<Guid, JoinReadinessSnapshot>
            {
                [joinNodeId] = snapshot
            });

        plan.Intents.Count.ShouldBe(0);
        plan.JoinSourceObservations.Single().Source.BindingId.ShouldBe("right-to-join");
    }

    [Test]
    public void Collection_iterator_fan_out_produces_item_scoped_intents_with_inline_inputs()
    {
        var processId = Guid.NewGuid();
        var iteratorNodeId = Guid.NewGuid();
        var destinationNodeId = Guid.NewGuid();
        var blueprint = CreateCollectionIteratorBlueprint(
            iteratorNodeId,
            destinationNodeId,
            "iterator-to-destination");
        var sourceStepId = ProcessStepIdentity.Create(processId, iteratorNodeId);

        var plan = new ActivationPlanner().PlanCollectionIteratorItemActivations(
            new CollectionIteratorActivationPlanningRequest(
                blueprint,
                blueprint.NodesById[iteratorNodeId],
                processId,
                sourceStepId,
                [Json("alpha"), Json("bravo")],
                new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)));

        plan.JoinSourceObservations.Count.ShouldBe(0);
        plan.Intents.Count.ShouldBe(2);
        for (var index = 0; index < plan.Intents.Count; index++)
        {
            var itemKey = $"{sourceStepId:N}:{index:D8}";
            var intent = plan.Intents[index];
            intent.ProcessStepId.ShouldBe(ProcessStepIdentity.Create(processId, destinationNodeId, itemKey));
            intent.IterationContext.ShouldNotBeNull();
            intent.IterationContext.ItemIndex.ShouldBe(index);
            intent.IterationContext.ItemKey.ShouldBe(itemKey);
            intent.InputHydrationPlan.Sources.Single().InlineInput.ShouldNotBeNull();
            intent.InputHydrationPlan.Sources.Single().InlineInput!["Text"].GetString()
                .ShouldBe(index == 0 ? "alpha" : "bravo");
        }
    }

    [Test]
    public void Planned_hydration_preserves_design_time_and_runtime_overrides()
    {
        var processId = Guid.NewGuid();
        var sourceNodeId = Guid.NewGuid();
        var destinationNodeId = Guid.NewGuid();
        var blueprint = CreateSingleInboundBlueprint(sourceNodeId, destinationNodeId, "source-to-destination");
        blueprint.NodesById[destinationNodeId].DesignTimeInputOverrides["Mode"] = Json("design");
        var sourceStepId = ProcessStepIdentity.Create(processId, sourceNodeId);

        var plan = PlanReadyActivations(
            blueprint,
            sourceNodeId,
            processId,
            sourceStepId,
            CreateResult(sourceStepId, "Out"),
            runtimeNodeOverrides: new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                [destinationNodeId.ToString()] = new(StringComparer.Ordinal)
                {
                    ["Mode"] = Json("runtime")
                }
            });

        var hydration = plan.Intents.Single().InputHydrationPlan;
        hydration.DesignTimeInputOverrides["Mode"].GetString().ShouldBe("design");
        hydration.RuntimeInputOverrides["Mode"].GetString().ShouldBe("runtime");
    }

    private static ActivationPlan PlanReadyActivations(
        ExecutableBlueprint blueprint,
        Guid currentNodeId,
        Guid processId,
        Guid sourceStepId,
        PluginExecutionResult result,
        IReadOnlyDictionary<Guid, JoinReadinessSnapshot>? joinReadinessSnapshots = null,
        IReadOnlyDictionary<string, Dictionary<string, JsonElement>>? runtimeNodeOverrides = null) =>
        new ActivationPlanner().PlanReadyActivations(new ActivationPlanningRequest(
            blueprint,
            blueprint.NodesById[currentNodeId],
            processId,
            sourceStepId,
            result,
            null,
            runtimeNodeOverrides ?? new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal),
            joinReadinessSnapshots));

    private static PluginExecutionResult CreateResult(Guid processStepId, string outputChannelName) =>
        new(
            processStepId,
            Succeeded: true,
            EffectiveInputData: null,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [outputChannelName] = Json(new
                {
                    Message = "hello",
                    Score = 7
                })
            },
            GlobalState: null,
            StartedAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAt: DateTimeOffset.UtcNow,
            TimingsMs: null,
            ErrorMessage: null);

    private static ExecutableBlueprint CreateSingleInboundBlueprint(
        Guid sourceNodeId,
        Guid destinationNodeId,
        string bindingId)
    {
        var binding = new ExecutableBlueprintBinding
        {
            Id = bindingId,
            SourceBlueprintNodeId = sourceNodeId,
            SourceChannelName = "Out",
            DestinationBlueprintNodeId = destinationNodeId,
            PropertyMappings =
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "Message",
                    TargetProperty = "Text"
                }
            ]
        };

        var source = CreateNode(sourceNodeId, "SourceInput", "Out", [binding]);
        var destination = CreateNode(destinationNodeId, "DestinationInput", null, [], [binding]);
        return CreateBlueprint(source, [source, destination]);
    }

    private static ExecutableBlueprint CreateJoinBlueprint(
        Guid leftNodeId,
        Guid rightNodeId,
        Guid joinNodeId)
    {
        var leftBinding = new ExecutableBlueprintBinding
        {
            Id = "left-to-join",
            SourceBlueprintNodeId = leftNodeId,
            SourceChannelName = "LeftOut",
            DestinationBlueprintNodeId = joinNodeId,
            PropertyMappings =
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "Message",
                    TargetProperty = "LeftText"
                }
            ]
        };
        var rightBinding = new ExecutableBlueprintBinding
        {
            Id = "right-to-join",
            SourceBlueprintNodeId = rightNodeId,
            SourceChannelName = "RightOut",
            DestinationBlueprintNodeId = joinNodeId,
            PropertyMappings =
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "Score",
                    TargetProperty = "Score"
                }
            ]
        };

        var left = CreateNode(leftNodeId, "LeftInput", "LeftOut", [leftBinding]);
        var right = CreateNode(rightNodeId, "RightInput", "RightOut", [rightBinding]);
        var join = CreateNode(joinNodeId, "JoinInput", null, [], [leftBinding, rightBinding]);
        return CreateBlueprint(left, [left, right, join]);
    }

    private static ExecutableBlueprint CreateCollectionIteratorBlueprint(
        Guid iteratorNodeId,
        Guid destinationNodeId,
        string bindingId)
    {
        var binding = new ExecutableBlueprintBinding
        {
            Id = bindingId,
            SourceBlueprintNodeId = iteratorNodeId,
            SourceChannelName = "Item",
            DestinationBlueprintNodeId = destinationNodeId,
            PropertyMappings =
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "item",
                    TargetProperty = "Text"
                }
            ]
        };
        var iterator = new ExecutableBlueprintNode
        {
            Id = iteratorNodeId,
            Name = "Collection Iterator",
            NodeKind = ExecutableBlueprintNodeKind.CollectionIterator,
            TaskDefinitionId = 0,
            CollectionIteratorItemKind = "String",
            AllowedMajor = 0,
            AllowedMinor = 0,
            ResolvedVersion = new Version(0, 0, 0),
            ResolvedTaskVersionId = 0,
            InputChannel = new ExecutableBlueprintChannel
            {
                Id = 0,
                Name = "Collection",
                Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                OutboundBindings = []
            },
            OutputChannels = new Dictionary<string, ExecutableBlueprintChannel>(StringComparer.Ordinal)
            {
                ["Item"] = new()
                {
                    Id = 0,
                    Name = "Item",
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["item"] = "String"
                    },
                    OutboundBindings = [binding]
                }
            },
            InboundBindings = [],
            DesignTimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        };
        var destination = CreateNode(destinationNodeId, "DestinationInput", null, [], [binding]);
        return CreateBlueprint(iterator, [iterator, destination]);
    }

    private static ExecutableBlueprintNode CreateNode(
        Guid nodeId,
        string inputChannelName,
        string? outputChannelName,
        List<ExecutableBlueprintBinding> outboundBindings,
        List<ExecutableBlueprintBinding>? inboundBindings = null)
    {
        var outputChannels = new Dictionary<string, ExecutableBlueprintChannel>(StringComparer.Ordinal);
        if (outputChannelName is not null)
        {
            outputChannels[outputChannelName] = new ExecutableBlueprintChannel
            {
                Id = outputChannels.Count + 1,
                Name = outputChannelName,
                Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                OutboundBindings = outboundBindings
            };
        }

        return new ExecutableBlueprintNode
        {
            Id = nodeId,
            Name = inputChannelName,
            TaskDefinitionId = 10,
            AllowedMajor = 1,
            AllowedMinor = 0,
            ResolvedVersion = new Version(1, 0, 0),
            ResolvedTaskVersionId = 100,
            ExecutionOrder = 0,
            InputChannel = new ExecutableBlueprintChannel
            {
                Id = 1,
                Name = inputChannelName,
                Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                OutboundBindings = []
            },
            OutputChannels = outputChannels,
            InboundBindings = inboundBindings ?? [],
            DesignTimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        };
    }

    private static ExecutableBlueprint CreateBlueprint(
        ExecutableBlueprintNode initialNode,
        List<ExecutableBlueprintNode> nodes) =>
        new()
        {
            Id = 101,
            DefinitionId = 10,
            VersionNumber = 1,
            Name = "Activation Blueprint",
            InitialNode = initialNode,
            AllNodes = nodes
        };

    private static JsonElement Json<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
}

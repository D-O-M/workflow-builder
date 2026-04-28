using System;
using System.Diagnostics;
using Backend.Database.State.Objects;
using Backend.Plugins.Communication;
using Backend.Plugins.Harness;
using Shouldly;

namespace Backend.Plugins.Harness.Tests;

public sealed class PluginInvocationTelemetryTests
{
    [Test]
    public void InvocationEnvelopeSerializer_round_trips_trace_context()
    {
        using var listener = CreateListener();
        using var parentSource = new ActivitySource("WorkflowBuilder.PluginTelemetry.Tests");
        using var parent = parentSource.StartActivity("parent", ActivityKind.Internal);
        parent.ShouldNotBeNull();

        var envelope = CreateEnvelope() with
        {
            TelemetryContext = InvocationTelemetryContext.FromActivity(parent)
        };

        var serialized = InvocationEnvelopeSerializer.Serialize(envelope);
        var deserialized = InvocationEnvelopeSerializer.Deserialize(serialized.Span);

        deserialized.TelemetryContext.ShouldNotBeNull();
        deserialized.TelemetryContext.TraceParent.ShouldBe(envelope.TelemetryContext!.TraceParent);
        deserialized.TelemetryContext.TraceState.ShouldBe(envelope.TelemetryContext.TraceState);
    }

    [Test]
    public void StartExecutionActivity_uses_engine_trace_context_as_parent()
    {
        using var listener = CreateListener();
        using var parentSource = new ActivitySource("WorkflowBuilder.PluginTelemetry.Tests");
        using var parent = parentSource.StartActivity("process.step", ActivityKind.Consumer);
        parent.ShouldNotBeNull();

        var envelope = CreateEnvelope() with
        {
            TelemetryContext = InvocationTelemetryContext.FromActivity(parent)
        };

        using var child = PluginInvocationTelemetry.StartExecutionActivity(envelope, envelope.PluginName);

        child.ShouldNotBeNull();
        child.TraceId.ShouldBe(parent.TraceId);
        child.ParentSpanId.ShouldBe(parent.SpanId);
        child.GetTagItem("process.id").ShouldBe(envelope.ProcessId.ToString());
        child.GetTagItem("process.step_id").ShouldBe(envelope.ProcessStepId.ToString());
    }

    [Test]
    public void BuildExecutionScope_uses_engine_process_label_conventions()
    {
        var envelope = CreateEnvelope();

        var scope = PluginInvocationTelemetry.BuildExecutionScope(envelope, envelope.PluginName);

        scope["process.id"].ShouldBe(envelope.ProcessId);
        scope["process.step_id"].ShouldBe(envelope.ProcessStepId);
        scope["workflow.instance_id"].ShouldBe(envelope.ProcessId);
        scope["workflow.task_instance_id"].ShouldBe(envelope.ProcessStepId);
        scope["plugin.name"].ShouldBe(envelope.PluginName);
    }

    private static ActivityListener CreateListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name is "WorkflowBuilder.PluginExecution" or "WorkflowBuilder.PluginTelemetry.Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static InvocationEnvelope CreateEnvelope() =>
        new(
            ExecutableBlueprintId: 100,
            BlueprintDefinitionId: 10,
            ProcessId: Guid.NewGuid(),
            ProcessStepId: Guid.NewGuid(),
            BlueprintNodeId: Guid.NewGuid(),
            TaskDefinitionId: 7,
            TaskVersionId: 70,
            TaskVersion: "1.0.0",
            PluginName: "TelemetryProbe",
            InputHydrationPlan: new InputHydrationPlan
            {
                InputChannelName = "Input"
            });
}

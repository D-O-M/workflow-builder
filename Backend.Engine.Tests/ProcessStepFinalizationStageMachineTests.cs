using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Database.State.Objects;
using Backend.Engine;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class ProcessStepFinalizationStageMachineTests
{
    [Test]
    public void Success_path_allows_only_success_stage_progression()
    {
        AssertCanAdvance(
            ProcessStepFinalizationOutcome.Succeeded,
            ProcessStepFinalizationStage.ResultReceived,
            ProcessStepFinalizationStage.OutputsPersisted);
        AssertCanAdvance(
            ProcessStepFinalizationOutcome.Succeeded,
            ProcessStepFinalizationStage.OutputsPersisted,
            ProcessStepFinalizationStage.DownstreamActivationCompleted);
        AssertCanAdvance(
            ProcessStepFinalizationOutcome.Succeeded,
            ProcessStepFinalizationStage.DownstreamActivationCompleted,
            ProcessStepFinalizationStage.ProcessFinalizationCompleted);
        AssertCanAdvance(
            ProcessStepFinalizationOutcome.Succeeded,
            ProcessStepFinalizationStage.ProcessFinalizationCompleted,
            ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
        AssertCanAdvance(
            ProcessStepFinalizationOutcome.Succeeded,
            ProcessStepFinalizationStage.QueueMessageCompletionPrepared,
            ProcessStepFinalizationStage.RecoveredWithoutQueueMessage);
        AssertCanAdvance(
            ProcessStepFinalizationOutcome.Succeeded,
            ProcessStepFinalizationStage.ProcessFinalizationCompleted,
            ProcessStepFinalizationStage.RecoveredWithoutQueueMessage);
    }

    [Test]
    public void Failure_path_allows_only_failure_stage_progression()
    {
        AssertCanAdvance(
            ProcessStepFinalizationOutcome.Failed,
            ProcessStepFinalizationStage.ResultReceived,
            ProcessStepFinalizationStage.FailurePersisted);
        AssertCanAdvance(
            ProcessStepFinalizationOutcome.Failed,
            ProcessStepFinalizationStage.FailurePersisted,
            ProcessStepFinalizationStage.ProcessFinalizationCompleted);
        AssertCanAdvance(
            ProcessStepFinalizationOutcome.Failed,
            ProcessStepFinalizationStage.ProcessFinalizationCompleted,
            ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
        AssertCanAdvance(
            ProcessStepFinalizationOutcome.Failed,
            ProcessStepFinalizationStage.QueueMessageCompletionPrepared,
            ProcessStepFinalizationStage.RecoveredWithoutQueueMessage);
        AssertCanAdvance(
            ProcessStepFinalizationOutcome.Failed,
            ProcessStepFinalizationStage.ProcessFinalizationCompleted,
            ProcessStepFinalizationStage.RecoveredWithoutQueueMessage);
    }

    [Test]
    public void Cross_path_transitions_are_rejected()
    {
        Should.Throw<InvalidOperationException>(() =>
                ProcessStepFinalizationStageMachine.EnsureCanAdvance(
                    ProcessStepFinalizationOutcome.Succeeded,
                    ProcessStepFinalizationStage.ResultReceived,
                    ProcessStepFinalizationStage.FailurePersisted))
            .Message.ShouldContain("not valid");

        Should.Throw<InvalidOperationException>(() =>
                ProcessStepFinalizationStageMachine.EnsureCanAdvance(
                    ProcessStepFinalizationOutcome.Failed,
                    ProcessStepFinalizationStage.ResultReceived,
                    ProcessStepFinalizationStage.OutputsPersisted))
            .Message.ShouldContain("not valid");

        Should.Throw<InvalidOperationException>(() =>
                ProcessStepFinalizationStageMachine.EnsureCanAdvance(
                    ProcessStepFinalizationOutcome.Failed,
                    ProcessStepFinalizationStage.ProcessFinalizationCompleted,
                    ProcessStepFinalizationStage.DownstreamActivationCompleted))
            .Message.ShouldContain("not valid");
    }

    [Test]
    public void Backwards_and_skipped_transitions_are_rejected()
    {
        Should.Throw<InvalidOperationException>(() =>
                ProcessStepFinalizationStageMachine.EnsureCanAdvance(
                    ProcessStepFinalizationOutcome.Succeeded,
                    ProcessStepFinalizationStage.ResultReceived,
                    ProcessStepFinalizationStage.DownstreamActivationCompleted))
            .Message.ShouldContain("Illegal process-step finalization transition");

        Should.Throw<InvalidOperationException>(() =>
                ProcessStepFinalizationStageMachine.EnsureCanAdvance(
                    ProcessStepFinalizationOutcome.Failed,
                    ProcessStepFinalizationStage.ProcessFinalizationCompleted,
                    ProcessStepFinalizationStage.FailurePersisted))
            .Message.ShouldContain("Illegal process-step finalization transition");
    }

    [Test]
    public void HasReached_is_branch_aware()
    {
        ProcessStepFinalizationStageMachine.HasReached(
                ProcessStepFinalizationOutcome.Succeeded,
                ProcessStepFinalizationStage.DownstreamActivationCompleted,
                ProcessStepFinalizationStage.OutputsPersisted)
            .ShouldBeTrue();

        ProcessStepFinalizationStageMachine.HasReached(
                ProcessStepFinalizationOutcome.Failed,
                ProcessStepFinalizationStage.FailurePersisted,
                ProcessStepFinalizationStage.ProcessFinalizationCompleted)
            .ShouldBeFalse();

        Should.Throw<InvalidOperationException>(() =>
            ProcessStepFinalizationStageMachine.HasReached(
                ProcessStepFinalizationOutcome.Failed,
                ProcessStepFinalizationStage.FailurePersisted,
                ProcessStepFinalizationStage.OutputsPersisted));
    }

    [Test]
    public void Legacy_queue_message_completed_stage_is_terminal_prepared()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var legacyStage = (ProcessStepFinalizationStage)Enum.Parse(
            typeof(ProcessStepFinalizationStage),
            "QueueMessageCompleted");

        ProcessStepFinalizationStageMachine.IsTerminalPrepared(legacyStage).ShouldBeTrue();
        ProcessStepFinalizationStageMachine.IsTerminalPrepared(ProcessStepFinalizationStage.RecoveredWithoutQueueMessage)
            .ShouldBeTrue();
        ProcessStepFinalizationStageMachine.NormalizeTerminalStage(legacyStage)
            .ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
        JsonSerializer.Deserialize<ProcessStepFinalizationStage>(
                "\"QueueMessageCompleted\"",
                options)
            .ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
        JsonSerializer.Serialize(
                ProcessStepFinalizationStage.QueueMessageCompletionPrepared,
                options)
            .ShouldBe("\"QueueMessageCompletionPrepared\"");
    }

    private static void AssertCanAdvance(
        ProcessStepFinalizationOutcome outcome,
        ProcessStepFinalizationStage current,
        ProcessStepFinalizationStage target)
    {
        ProcessStepFinalizationStageMachine.EnsureCanAdvance(outcome, current, target);
        ProcessStepFinalizationStageMachine.HasReached(outcome, target, current).ShouldBeTrue();
    }
}

using Backend.Database.State.Objects;

namespace Backend.Engine;

public static class ProcessStepFinalizationStageMachine
{
    private static readonly ProcessStepFinalizationStage[] SuccessStages =
    [
        ProcessStepFinalizationStage.ResultReceived,
        ProcessStepFinalizationStage.OutputsPersisted,
        ProcessStepFinalizationStage.DownstreamActivationCompleted,
        ProcessStepFinalizationStage.ProcessFinalizationCompleted,
        ProcessStepFinalizationStage.QueueMessageCompletionPrepared,
        ProcessStepFinalizationStage.RecoveredWithoutQueueMessage
    ];

    private static readonly ProcessStepFinalizationStage[] FailureStages =
    [
        ProcessStepFinalizationStage.ResultReceived,
        ProcessStepFinalizationStage.FailurePersisted,
        ProcessStepFinalizationStage.ProcessFinalizationCompleted,
        ProcessStepFinalizationStage.QueueMessageCompletionPrepared,
        ProcessStepFinalizationStage.RecoveredWithoutQueueMessage
    ];

    public static bool HasReached(
        ProcessStepFinalizationOutcome outcome,
        ProcessStepFinalizationStage current,
        ProcessStepFinalizationStage target)
    {
        var stages = GetStages(outcome);
        var currentIndex = IndexOf(stages, NormalizeTerminalStage(current));
        var targetIndex = IndexOf(stages, NormalizeTerminalStage(target));
        return currentIndex >= targetIndex;
    }

    public static void EnsureCanAdvance(
        ProcessStepFinalizationOutcome outcome,
        ProcessStepFinalizationStage current,
        ProcessStepFinalizationStage target)
    {
        var stages = GetStages(outcome);
        var currentStage = NormalizeTerminalStage(current);
        var targetStage = NormalizeTerminalStage(target);
        var currentIndex = IndexOf(stages, currentStage);
        var targetIndex = IndexOf(stages, targetStage);

        if (targetIndex == currentIndex)
        {
            return;
        }

        if (targetIndex == currentIndex + 1)
        {
            return;
        }

        if (targetStage is ProcessStepFinalizationStage.RecoveredWithoutQueueMessage &&
            currentStage is (
                ProcessStepFinalizationStage.ProcessFinalizationCompleted or
                ProcessStepFinalizationStage.QueueMessageCompletionPrepared))
        {
            return;
        }

        var pathName = outcome is ProcessStepFinalizationOutcome.Succeeded
            ? "success"
            : "failure";
        throw new ProcessStepFinalizationTransitionException(
            $"Illegal process-step finalization transition on the {pathName} path: {currentStage} -> {targetStage}.");
    }

    public static bool IsTerminalPrepared(ProcessStepFinalizationStage stage) =>
        NormalizeTerminalStage(stage) is
            ProcessStepFinalizationStage.QueueMessageCompletionPrepared or
            ProcessStepFinalizationStage.RecoveredWithoutQueueMessage;

    public static bool IsQueueMessageCompletionPrepared(ProcessStepFinalizationStage stage) =>
        NormalizeTerminalStage(stage) is ProcessStepFinalizationStage.QueueMessageCompletionPrepared;

    public static bool IsRecoveredWithoutQueueMessage(ProcessStepFinalizationStage stage) =>
        NormalizeTerminalStage(stage) is ProcessStepFinalizationStage.RecoveredWithoutQueueMessage;

    public static ProcessStepFinalizationStage NormalizeTerminalStage(ProcessStepFinalizationStage stage) =>
        stage is ProcessStepFinalizationStage.QueueMessageCompletionPrepared
            ? ProcessStepFinalizationStage.QueueMessageCompletionPrepared
            : stage;

    private static ProcessStepFinalizationStage[] GetStages(ProcessStepFinalizationOutcome outcome) =>
        outcome switch
        {
            ProcessStepFinalizationOutcome.Succeeded => SuccessStages,
            ProcessStepFinalizationOutcome.Failed => FailureStages,
            _ => throw new InvalidOperationException($"Unsupported process-step finalization outcome '{outcome}'.")
        };

    private static int IndexOf(
        IReadOnlyList<ProcessStepFinalizationStage> stages,
        ProcessStepFinalizationStage stage)
    {
        for (var index = 0; index < stages.Count; index++)
        {
            if (stages[index] == stage)
            {
                return index;
            }
        }

        throw new ProcessStepFinalizationTransitionException(
            $"Finalization stage '{stage}' is not valid for this process-step finalization path.");
    }
}

public sealed class ProcessStepFinalizationTransitionException : InvalidOperationException
{
    public ProcessStepFinalizationTransitionException(string message)
        : base(message)
    {
    }
}

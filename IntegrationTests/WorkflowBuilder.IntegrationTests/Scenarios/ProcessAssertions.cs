using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Repositories;
using Shouldly;
using WorkflowBuilder.IntegrationTests.Infrastructure;

namespace WorkflowBuilder.IntegrationTests.Scenarios;

internal static class ProcessAssertions
{
    internal sealed record ProcessSnapshot(
        ProcessInstanceRecord<Dictionary<string, JsonElement>> Process,
        IReadOnlyList<ProcessStepRecord> Steps)
    {
        public IReadOnlyList<ProcessStepRecord> RealSteps =>
            Steps.Where(step => step.BlueprintNodeId != Guid.Empty.ToString()).ToList();
    }

    public static Task<ProcessSnapshot> WaitForSuccessfulChainAsync(
        DistributedAppTestEnvironment environment,
        Guid processId,
        int blueprintVersionId,
        int expectedRealStepCount,
        CancellationToken cancellationToken) =>
        WaitForSnapshotAsync(
            environment,
            processId,
            blueprintVersionId,
            snapshot =>
                snapshot.RealSteps.Count == expectedRealStepCount &&
                snapshot.RealSteps.All(step => step.Status == ProcessStepStatus.Completed),
            cancellationToken);

    public static Task<ProcessSnapshot> WaitForFailedChainAsync(
        DistributedAppTestEnvironment environment,
        Guid processId,
        int blueprintVersionId,
        int expectedRealStepCount,
        CancellationToken cancellationToken) =>
        WaitForSnapshotAsync(
            environment,
            processId,
            blueprintVersionId,
            snapshot =>
                snapshot.RealSteps.Count == expectedRealStepCount &&
                snapshot.RealSteps.Any(step => step.Status == ProcessStepStatus.Failed),
            cancellationToken);

    public static Task<ProcessSnapshot> WaitForRealStepsAsync(
        DistributedAppTestEnvironment environment,
        Guid processId,
        int blueprintVersionId,
        IReadOnlyDictionary<Guid, ProcessStepStatus> expectedSteps,
        CancellationToken cancellationToken) =>
        WaitForSnapshotAsync(
            environment,
            processId,
            blueprintVersionId,
            snapshot =>
                snapshot.RealSteps.Count == expectedSteps.Count &&
                expectedSteps.All(expected =>
                    snapshot.RealSteps.Count(step =>
                        step.BlueprintNodeId == expected.Key.ToString() &&
                        step.Status == expected.Value) == 1),
            cancellationToken);

    public static ProcessStepRecord GetRealStep(ProcessSnapshot snapshot, Guid blueprintNodeId) =>
        snapshot.RealSteps.Single(step => step.BlueprintNodeId == blueprintNodeId.ToString());

    public static void AssertNodeExecutedOnce(ProcessSnapshot snapshot, Guid blueprintNodeId)
    {
        snapshot.RealSteps.Count(step => step.BlueprintNodeId == blueprintNodeId.ToString()).ShouldBe(1);
    }

    public static void AssertNodeDidNotExecute(ProcessSnapshot snapshot, Guid blueprintNodeId)
    {
        snapshot.RealSteps.ShouldNotContain(step => step.BlueprintNodeId == blueprintNodeId.ToString());
    }

    public static JsonElement GetInput(ProcessStepRecord step, string channelName)
    {
        step.InputData.ShouldNotBeNull();
        step.InputData!.ContainsKey(channelName).ShouldBeTrue();
        return step.InputData[channelName];
    }

    public static JsonElement GetOutput(ProcessStepRecord step, string channelName)
    {
        step.OutputData.ShouldNotBeNull();
        step.OutputData!.ContainsKey(channelName).ShouldBeTrue();
        return step.OutputData[channelName];
    }

    private static async Task<ProcessSnapshot> WaitForSnapshotAsync(
        DistributedAppTestEnvironment environment,
        Guid processId,
        int blueprintVersionId,
        Func<ProcessSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(environment.Settings.ResourceTimeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                var steps = await environment.GetProcessStepStateAsync(processId, timeoutCts.Token);
                var processRecord = await environment.GetProcessStateAsync(
                    processId,
                    blueprintVersionId,
                    timeoutCts.Token);

                var snapshot = new ProcessSnapshot(processRecord, steps);
                if (predicate(snapshot))
                {
                    return snapshot;
                }
            }
            catch (EntityNotFoundException<ProcessInstanceRecord<Dictionary<string, JsonElement>>>)
            {
                // The process document can land slightly after the start response.
            }

            await Task.Delay(environment.Settings.PollInterval, timeoutCts.Token);
        }

        throw new TimeoutException($"Timed out waiting for process {processId} state.");
    }
}

using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Repositories;
using Shouldly;
using WorkflowBuilder.IntegrationTests.Infrastructure;

namespace WorkflowBuilder.IntegrationTests.Scenarios;

internal static class WorkflowAssertions
{
    internal sealed record WorkflowSnapshot(
        WorkflowObject<Dictionary<string, JsonElement>> Workflow,
        IReadOnlyList<TaskObject> Tasks)
    {
        public IReadOnlyList<TaskObject> RealTasks =>
            Tasks.Where(task => task.BlueprintTaskId != Guid.Empty.ToString()).ToList();
    }

    public static Task<WorkflowSnapshot> WaitForSuccessfulChainAsync(
        DistributedAppTestEnvironment environment,
        Guid workflowInstanceId,
        int blueprintVersionId,
        int expectedRealTaskCount,
        CancellationToken cancellationToken) =>
        WaitForSnapshotAsync(
            environment,
            workflowInstanceId,
            blueprintVersionId,
            snapshot =>
                snapshot.RealTasks.Count == expectedRealTaskCount &&
                snapshot.RealTasks.All(task => task.Status == TaskExecutionStatus.Completed),
            cancellationToken);

    public static Task<WorkflowSnapshot> WaitForFailedChainAsync(
        DistributedAppTestEnvironment environment,
        Guid workflowInstanceId,
        int blueprintVersionId,
        int expectedRealTaskCount,
        CancellationToken cancellationToken) =>
        WaitForSnapshotAsync(
            environment,
            workflowInstanceId,
            blueprintVersionId,
            snapshot =>
                snapshot.RealTasks.Count == expectedRealTaskCount &&
                snapshot.RealTasks.Any(task => task.Status == TaskExecutionStatus.Failed),
            cancellationToken);

    public static JsonElement GetOutput(TaskObject task, string channelName)
    {
        task.OutputData.ShouldNotBeNull();
        task.OutputData!.ContainsKey(channelName).ShouldBeTrue();
        return task.OutputData[channelName];
    }

    private static async Task<WorkflowSnapshot> WaitForSnapshotAsync(
        DistributedAppTestEnvironment environment,
        Guid workflowInstanceId,
        int blueprintVersionId,
        Func<WorkflowSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(environment.Settings.ResourceTimeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                var tasks = await environment.GetTaskStateAsync(workflowInstanceId, timeoutCts.Token);
                var workflow = await environment.GetWorkflowStateAsync(
                    workflowInstanceId,
                    blueprintVersionId,
                    timeoutCts.Token);

                var snapshot = new WorkflowSnapshot(workflow, tasks);
                if (predicate(snapshot))
                {
                    return snapshot;
                }
            }
            catch (EntityNotFoundException<WorkflowObject<Dictionary<string, JsonElement>>>)
            {
                // The workflow document can land slightly after the execute response.
            }

            await Task.Delay(environment.Settings.PollInterval, timeoutCts.Token);
        }

        throw new TimeoutException($"Timed out waiting for workflow {workflowInstanceId} state.");
    }
}

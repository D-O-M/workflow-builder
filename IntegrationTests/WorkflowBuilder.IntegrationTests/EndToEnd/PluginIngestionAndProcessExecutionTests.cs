using System.Net.Http.Json;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Models.InputModels;
using Backend.Topology;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using WorkflowBuilder.IntegrationTests.Infrastructure;
using WorkflowBuilder.IntegrationTests.Scenarios;

namespace WorkflowBuilder.IntegrationTests.EndToEnd;

[Timeout(900_000)]
public sealed class PluginIngestionAndProcessExecutionTests
{
    private static DistributedAppTestEnvironment _environment = null!;

    [Before(Class)]
    public static async Task SetUp(CancellationToken cancellationToken)
    {
        _environment = await SharedDistributedAppTestEnvironment.GetReadyAsync(cancellationToken);
    }

    [Test]
    public async Task Plugins_are_registered_through_the_real_ingestion_pipeline(
        CancellationToken cancellationToken)
    {
        var tasks = await _environment.GetTasksAsync(cancellationToken);

        tasks.ShouldContain(task =>
            task.Name == PluginBundleCatalog.SeedPlugin &&
            task.Versions.Any(version => VersionsMatch(version.Version, _environment.Settings.RunVersion)));
        tasks.ShouldContain(task =>
            task.Name == PluginBundleCatalog.TransformPlugin &&
            task.Versions.Any(version => VersionsMatch(version.Version, _environment.Settings.RunVersion)));
        tasks.ShouldContain(task =>
            task.Name == PluginBundleCatalog.ResultPlugin &&
            task.Versions.Any(version => VersionsMatch(version.Version, _environment.Settings.RunVersion)));
        tasks.ShouldContain(task =>
            task.Name == PluginBundleCatalog.FailurePlugin &&
            task.Versions.Any(version => VersionsMatch(version.Version, _environment.Settings.RunVersion)));
        tasks.ShouldContain(task =>
            task.Name == PluginBundleCatalog.JoinProbePlugin &&
            task.Versions.Any(version => VersionsMatch(version.Version, _environment.Settings.RunVersion)));
    }

    [Test]
    public async Task Uploading_the_same_bundle_twice_does_not_create_duplicate_versions(
        CancellationToken cancellationToken)
    {
        await _environment.UploadBundleAsync(
            PluginBundleCatalog.SeedPlugin,
            _environment.Settings.RunVersion,
            cancellationToken);

        await _environment.WaitForPluginRegistrationAsync(cancellationToken);

        var tasks = await _environment.GetTasksAsync(cancellationToken);
        var matchingVersions = tasks
            .Single(task => task.Name == PluginBundleCatalog.SeedPlugin)
            .Versions
            .Where(version => VersionsMatch(version.Version, _environment.Settings.RunVersion))
            .ToList();

        matchingVersions.Count.ShouldBe(1);
    }

    [Test]
    public async Task A_versioned_blueprint_executes_registered_plugins_end_to_end(
        CancellationToken cancellationToken)
    {
        var blueprint = await BlueprintScenarioBuilder.CreateHappyPathAsync(
            _environment,
            cancellationToken);

        var response = await _environment.ApiClient.PostAsJsonAsync(
            $"/api/blueprints/{blueprint.BlueprintDefinitionId}/processes",
            new BlueprintProcessInputModel
            {
                InitialTaskInput = ToDictionary(new
                {
                    name = "  caveman  ",
                    baseScore = 4
                })
            },
            cancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(cancellationToken));

        var accepted = await response.Content.ReadFromJsonAsync<AcceptedProcess>(cancellationToken: cancellationToken);
        accepted.ShouldNotBeNull();

        var snapshot = await ProcessAssertions.WaitForSuccessfulChainAsync(
            _environment,
            accepted.ProcessId,
            blueprint.BlueprintVersionId,
            expectedRealStepCount: 3,
            cancellationToken);

        snapshot.Process.Status.ShouldBe(ProcessStatus.Completed);
        snapshot.RealSteps
            .All(step => step.Dispatch?.Status == ProcessStepDispatchStatus.Dispatched)
            .ShouldBeTrue();

        var finalStep = snapshot.RealSteps.Single(step => step.BlueprintNodeId == blueprint.FinalBlueprintNodeId.ToString());
        var finalOutput = ProcessAssertions.GetOutput(finalStep, "Success");
        finalOutput.GetProperty("Summary").GetString().ShouldBe("Completed CAVEMAN:15");
        finalOutput.GetProperty("FinalScore").GetInt32().ShouldBe(15);
    }

    [Test]
    public async Task Process_progress_is_broadcast_over_signalr_for_the_selected_blueprint_and_process(
        CancellationToken cancellationToken)
    {
        var blueprint = await BlueprintScenarioBuilder.CreateHappyPathAsync(
            _environment,
            cancellationToken);

        var summaryUpdates = new List<JsonElement>();
        var nodeUpdates = new List<JsonElement>();
        var gate = new object();

        await using var connection = _environment.CreateSignalRConnection();
        connection.On<JsonElement>(WorkflowBuilderTopology.Realtime.ProcessSummaryUpdatedEvent, update =>
        {
            lock (gate)
            {
                summaryUpdates.Add(update.Clone());
            }
        });
        connection.On<JsonElement>(WorkflowBuilderTopology.Realtime.ProcessStepsUpdatedEvent, update =>
        {
            lock (gate)
            {
                nodeUpdates.Add(update.Clone());
            }
        });

        await connection.StartAsync(cancellationToken);
        await connection.InvokeAsync("SubscribeBlueprint", blueprint.BlueprintDefinitionId, cancellationToken);

        var response = await _environment.ApiClient.PostAsJsonAsync(
            $"/api/blueprints/{blueprint.BlueprintDefinitionId}/processes",
            new BlueprintProcessInputModel
            {
                InitialTaskInput = ToDictionary(new
                {
                    name = "realtime",
                    baseScore = 3
                })
            },
            cancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(cancellationToken));

        var accepted = await response.Content.ReadFromJsonAsync<AcceptedProcess>(cancellationToken: cancellationToken);
        accepted.ShouldNotBeNull();

        await connection.InvokeAsync("SubscribeProcess", accepted.ProcessId, cancellationToken);

        var snapshot = await ProcessAssertions.WaitForSuccessfulChainAsync(
            _environment,
            accepted.ProcessId,
            blueprint.BlueprintVersionId,
            expectedRealStepCount: 3,
            cancellationToken);

        lock (gate)
        {
            summaryUpdates.Clear();
            nodeUpdates.Clear();
        }

        var finalStep = snapshot.RealSteps.Single(step => step.BlueprintNodeId == blueprint.FinalBlueprintNodeId.ToString());
        finalStep.UpdatedAt = DateTimeOffset.UtcNow;
        await _environment.UpdateProcessStepStateAsync(finalStep, cancellationToken);

        try
        {
            await WaitForAsync(
                () =>
                {
                    lock (gate)
                    {
                        return summaryUpdates.Any(update =>
                            update.GetProperty("processId").GetString() == accepted.ProcessId.ToString())
                            && nodeUpdates.Any(update =>
                                update.GetProperty("processId").GetString() == accepted.ProcessId.ToString());
                    }
                },
                cancellationToken);
        }
        catch
        {
            _environment.WriteRecentResourceLogs(
                "SignalR process progress was not observed after touching a process step",
                "progress-broadcaster",
                "backend-signalr",
                "backend-api",
                "backend-engine",
                "integration-cosmos");
            throw;
        }
    }

    [Test]
    public async Task A_failing_plugin_marks_the_process_failed_and_does_not_enqueue_children(
        CancellationToken cancellationToken)
    {
        var blueprint = await BlueprintScenarioBuilder.CreateFailurePathAsync(
            _environment,
            cancellationToken);

        var response = await _environment.ApiClient.PostAsJsonAsync(
            $"/api/blueprints/{blueprint.BlueprintDefinitionId}/processes",
            new BlueprintProcessInputModel
            {
                InitialTaskInput = ToDictionary(new
                {
                    name = "doomed",
                    baseScore = 2
                })
            },
            cancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(cancellationToken));

        var accepted = await response.Content.ReadFromJsonAsync<AcceptedProcess>(cancellationToken: cancellationToken);
        accepted.ShouldNotBeNull();

        var snapshot = await ProcessAssertions.WaitForFailedChainAsync(
            _environment,
            accepted.ProcessId,
            blueprint.BlueprintVersionId,
            expectedRealStepCount: 2,
            cancellationToken);

        snapshot.Process.Status.ShouldBe(ProcessStatus.Failed);
        snapshot.RealSteps.ShouldNotContain(step => step.BlueprintNodeId == blueprint.FinalBlueprintNodeId.ToString());
        snapshot.RealSteps.ShouldContain(step => step.Status == ProcessStepStatus.Failed);
    }

    private static Dictionary<string, JsonElement> ToDictionary<T>(T value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
            JsonSerializerOptions.Web)!;

    private static bool VersionsMatch(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.Equals(rightVersion);
        }

        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static async Task WaitForAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

        while (!timeoutCts.IsCancellationRequested)
        {
            if (predicate())
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException("Timed out waiting for SignalR process progress.");
    }

    private sealed record AcceptedProcess(Guid ProcessId, int BlueprintId, DateTimeOffset AcceptedAt);
}

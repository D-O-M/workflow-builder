using System.Net.Http.Json;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Models.InputModels;
using Shouldly;
using WorkflowBuilder.IntegrationTests.Infrastructure;
using WorkflowBuilder.IntegrationTests.Scenarios;

namespace WorkflowBuilder.IntegrationTests.EndToEnd;

[Timeout(900_000)]
public sealed class PluginIngestionAndWorkflowExecutionTests
{
    private static DistributedAppTestEnvironment _environment = null!;

    [Before(Class)]
    public static async Task SetUp(CancellationToken cancellationToken)
    {
        _environment = await DistributedAppTestEnvironment.CreateReadyAsync(cancellationToken);
    }

    [After(Class)]
    public static async Task TearDown(CancellationToken cancellationToken)
    {
        if (_environment is not null)
        {
            await _environment.DisposeAsync();
        }
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
            $"/api/blueprints/{blueprint.BlueprintDefinitionId}/execute",
            new BlueprintExecutionInputModel
            {
                GlobalState = ToDictionary(new
                {
                    runLabel = "happy-path",
                    stepsCompleted = 0,
                    totalScore = 0,
                    lastStage = "queued",
                    shouldFail = false
                }),
                InitialTaskInput = ToDictionary(new
                {
                    name = "  caveman  ",
                    baseScore = 4
                })
            },
            cancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(cancellationToken));

        var accepted = await response.Content.ReadFromJsonAsync<AcceptedExecution>(cancellationToken: cancellationToken);
        accepted.ShouldNotBeNull();

        var snapshot = await WorkflowAssertions.WaitForSuccessfulChainAsync(
            _environment,
            accepted.WorkflowInstanceId,
            blueprint.BlueprintVersionId,
            expectedRealTaskCount: 3,
            cancellationToken);

        snapshot.Workflow.Status.ShouldBe(WorkflowExecutionStatus.InProgress);
        snapshot.Workflow.GlobalState["stepsCompleted"].GetInt32().ShouldBe(3);
        snapshot.Workflow.GlobalState["totalScore"].GetInt32().ShouldBe(20);
        snapshot.Workflow.GlobalState["lastStage"].GetString().ShouldBe("result");

        var finalTask = snapshot.RealTasks.Single(task => task.BlueprintTaskId == blueprint.FinalTaskInstanceId.ToString());
        var finalOutput = WorkflowAssertions.GetOutput(finalTask, "Success");
        finalOutput.GetProperty("summary").GetString().ShouldBe("Completed CAVEMAN:15");
        finalOutput.GetProperty("finalScore").GetInt32().ShouldBe(15);
    }

    [Test]
    public async Task A_failing_plugin_marks_the_workflow_failed_and_does_not_enqueue_children(
        CancellationToken cancellationToken)
    {
        var blueprint = await BlueprintScenarioBuilder.CreateFailurePathAsync(
            _environment,
            cancellationToken);

        var response = await _environment.ApiClient.PostAsJsonAsync(
            $"/api/blueprints/{blueprint.BlueprintDefinitionId}/execute",
            new BlueprintExecutionInputModel
            {
                GlobalState = ToDictionary(new
                {
                    runLabel = "failure-path",
                    stepsCompleted = 0,
                    totalScore = 0,
                    lastStage = "queued",
                    shouldFail = true
                }),
                InitialTaskInput = ToDictionary(new
                {
                    name = "doomed",
                    baseScore = 2
                })
            },
            cancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(cancellationToken));

        var accepted = await response.Content.ReadFromJsonAsync<AcceptedExecution>(cancellationToken: cancellationToken);
        accepted.ShouldNotBeNull();

        var snapshot = await WorkflowAssertions.WaitForFailedChainAsync(
            _environment,
            accepted.WorkflowInstanceId,
            blueprint.BlueprintVersionId,
            expectedRealTaskCount: 2,
            cancellationToken);

        snapshot.Workflow.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        snapshot.Workflow.GlobalState["lastStage"].GetString().ShouldBe("failure");
        snapshot.RealTasks.ShouldNotContain(task => task.BlueprintTaskId == blueprint.FinalTaskInstanceId.ToString());
        snapshot.RealTasks.ShouldContain(task => task.Status == TaskExecutionStatus.Failed);
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

    private sealed record AcceptedExecution(Guid WorkflowInstanceId, int BlueprintId, DateTimeOffset AcceptedAt);
}

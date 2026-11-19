using System.Net.Http.Json;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Models.InputModels;
using Shouldly;
using WorkflowBuilder.IntegrationTests.Infrastructure;
using WorkflowBuilder.IntegrationTests.Scenarios;

namespace WorkflowBuilder.IntegrationTests.EndToEnd;

[Timeout(900_000)]
public sealed class BranchingBlueprintExecutionTests
{
    private static DistributedAppTestEnvironment _environment = null!;

    [Before(Class)]
    public static async Task SetUp(CancellationToken cancellationToken)
    {
        _environment = await SharedDistributedAppTestEnvironment.GetReadyAsync(cancellationToken);
    }

    [Test]
    public async Task Exclusive_outputs_and_alternative_plugin_results_rejoin_once_on_either_route(
        CancellationToken cancellationToken)
    {
        var graph = await BlueprintScenarioBuilder.CreateConditionalScenarioAsync(_environment, cancellationToken);
        foreach (var (baseScore, selected, selectedId, skippedId) in new[]
                 { (5, "True", graph.TrueTask, graph.FalseTask), (4, "False", graph.FalseTask, graph.TrueTask) })
        {
            var accepted = await StartProcessAsync(graph.DefinitionId, BlueprintScenarioBuilder.CreateProcessInput(
                new { name = "north", baseScore }), cancellationToken);
            var snapshot = await ProcessAssertions.WaitForSnapshotAsync(_environment, accepted.ProcessId, graph.VersionId,
                state => state.Process.Status is ProcessStatus.Completed or ProcessStatus.Failed, cancellationToken);

            snapshot.Process.Status.ShouldBe(ProcessStatus.Completed);
            snapshot.RealSteps.Count.ShouldBe(7);
            foreach (var nodeId in new[] { graph.Seed, graph.DirectBranch, graph.Transform, graph.Branch, selectedId, graph.SharedTask })
                ProcessAssertions.GetRealStep(snapshot, nodeId).Status.ShouldBe(ProcessStepStatus.Completed);
            ProcessAssertions.GetRealStep(snapshot, skippedId).Status.ShouldBe(ProcessStepStatus.Skipped);

            var direct = ProcessAssertions.GetRealStep(snapshot, graph.DirectBranch);
            direct.OutputData!.Keys.ShouldBe([selected]);
            ProcessAssertions.GetOutput(direct, selected).GetProperty("Score").GetInt32().ShouldBe(baseScore + 1);
            var decision = ProcessAssertions.GetRealStep(snapshot, graph.Branch);
            decision.OutputData!.Keys.ShouldBe([selected]);
            var finalScore = (baseScore + 1) * 3;
            var shared = ProcessAssertions.GetRealStep(snapshot, graph.SharedTask);
            ProcessAssertions.GetInput(shared, "Request").GetProperty("Message").GetString().ShouldBe($"Completed NORTH:{finalScore}");
            ProcessAssertions.GetOutput(shared, "Success").GetProperty("FinalScore").GetInt32().ShouldBe(finalScore);
        }
    }

    [Test]
    public async Task Fan_out_executes_each_downstream_node_once(
        CancellationToken cancellationToken)
    {
        var blueprint = await BlueprintScenarioBuilder.CreateFanOutScenarioAsync(
            _environment,
            cancellationToken);

        var accepted = await StartProcessAsync(
            blueprint.BlueprintDefinitionId,
            BlueprintScenarioBuilder.CreateProcessInput(
                new
                {
                    name = "  caveman  ",
                    baseScore = 4
                }),
            cancellationToken);

        var snapshot = await ProcessAssertions.WaitForRealStepsAsync(
            _environment,
            accepted.ProcessId,
            blueprint.BlueprintVersionId,
            new Dictionary<Guid, ProcessStepStatus>
            {
                [blueprint.SeedBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.LeftBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.RightBlueprintNodeId] = ProcessStepStatus.Completed,
            },
            cancellationToken);

        ProcessAssertions.AssertNodeExecutedOnce(snapshot, blueprint.LeftBlueprintNodeId);
        ProcessAssertions.AssertNodeExecutedOnce(snapshot, blueprint.RightBlueprintNodeId);

        var leftInput = ProcessAssertions.GetInput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.LeftBlueprintNodeId),
            "Request");
        var rightInput = ProcessAssertions.GetInput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.RightBlueprintNodeId),
            "Request");
        var leftOutput = ProcessAssertions.GetOutput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.LeftBlueprintNodeId),
            "Success");
        var rightOutput = ProcessAssertions.GetOutput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.RightBlueprintNodeId),
            "Success");

        leftInput.GetRawText().ShouldBe(rightInput.GetRawText());
        GetPropertyNames(leftInput).ShouldBe(["NormalizedName", "Score"]);
        leftInput.GetProperty("NormalizedName").GetString().ShouldBe("CAVEMAN");
        leftInput.GetProperty("Score").GetInt32().ShouldBe(5);
        leftOutput.GetProperty("Message").GetString().ShouldBe("CAVEMAN:15");
        leftOutput.GetProperty("FinalScore").GetInt32().ShouldBe(15);
        rightOutput.GetRawText().ShouldBe(leftOutput.GetRawText());
    }

    [Test]
    public async Task Join_waits_for_all_inbound_bindings_and_materializes_partial_property_transfer(
        CancellationToken cancellationToken)
    {
        var blueprint = await BlueprintScenarioBuilder.CreateJoinScenarioAsync(
            _environment,
            cancellationToken);

        var accepted = await StartProcessAsync(
            blueprint.BlueprintDefinitionId,
            BlueprintScenarioBuilder.CreateProcessInput(
                new
                {
                    name = "  caveman  ",
                    baseScore = 4
                }),
            cancellationToken);

        var snapshot = await ProcessAssertions.WaitForRealStepsAsync(
            _environment,
            accepted.ProcessId,
            blueprint.BlueprintVersionId,
            new Dictionary<Guid, ProcessStepStatus>
            {
                [blueprint.SeedBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.LeftBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.RightBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.JoinBlueprintNodeId] = ProcessStepStatus.Completed,
            },
            cancellationToken);

        var leftStep = ProcessAssertions.GetRealStep(snapshot, blueprint.LeftBlueprintNodeId);
        var rightStep = ProcessAssertions.GetRealStep(snapshot, blueprint.RightBlueprintNodeId);
        var joinStep = ProcessAssertions.GetRealStep(snapshot, blueprint.JoinBlueprintNodeId);
        var joinInput = ProcessAssertions.GetInput(joinStep, "Request");

        GetPropertyNames(joinInput).ShouldBe(["CombinedScore", "LeftText", "RightText"]);
        joinInput.GetProperty("LeftText").GetString().ShouldBe("CAVEMAN:15");
        joinInput.GetProperty("RightText").GetString().ShouldBe("RIGHT:21");
        joinInput.GetProperty("CombinedScore").GetInt32().ShouldBe(21);

        leftStep.CompletedAt.ShouldNotBeNull();
        rightStep.CompletedAt.ShouldNotBeNull();
        joinStep.CreatedAt.ShouldBeGreaterThanOrEqualTo(leftStep.CompletedAt.Value);
        joinStep.CreatedAt.ShouldBeGreaterThanOrEqualTo(rightStep.CompletedAt.Value);
        ProcessAssertions.GetOutput(joinStep, "Success").GetProperty("Summary").GetString().ShouldBe("CAVEMAN:15|RIGHT:21|21|");
    }

    [Test]
    public async Task Design_time_overrides_overlay_mapped_inputs(
        CancellationToken cancellationToken)
    {
        var blueprint = await BlueprintScenarioBuilder.CreateDesignTimeOverrideScenarioAsync(
            _environment,
            cancellationToken);

        var accepted = await StartProcessAsync(
            blueprint.BlueprintDefinitionId,
            BlueprintScenarioBuilder.CreateProcessInput(
                new
                {
                    name = "  caveman  ",
                    baseScore = 4
                }),
            cancellationToken);

        var snapshot = await ProcessAssertions.WaitForRealStepsAsync(
            _environment,
            accepted.ProcessId,
            blueprint.BlueprintVersionId,
            new Dictionary<Guid, ProcessStepStatus>
            {
                [blueprint.SeedBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.LeftBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.RightBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.JoinBlueprintNodeId] = ProcessStepStatus.Completed,
            },
            cancellationToken);

        var joinInput = ProcessAssertions.GetInput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.JoinBlueprintNodeId),
            "Request");

        GetPropertyNames(joinInput).ShouldBe(["CombinedScore", "LeftText", "Mode", "RightText"]);
        joinInput.GetProperty("LeftText").GetString().ShouldBe("DESIGN-LEFT");
        joinInput.GetProperty("RightText").GetString().ShouldBe("RIGHT:21");
        joinInput.GetProperty("CombinedScore").GetInt32().ShouldBe(21);
        joinInput.GetProperty("Mode").GetString().ShouldBe("design-mode");
        ProcessAssertions.GetOutput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.JoinBlueprintNodeId),
            "Success").GetProperty("Summary").GetString().ShouldBe("DESIGN-LEFT|RIGHT:21|21|design-mode");
    }

    [Test]
    public async Task Runtime_overrides_overlay_mapped_inputs(
        CancellationToken cancellationToken)
    {
        var blueprint = await BlueprintScenarioBuilder.CreateRuntimeOverrideScenarioAsync(
            _environment,
            cancellationToken);

        var accepted = await StartProcessAsync(
            blueprint.BlueprintDefinitionId,
            BlueprintScenarioBuilder.CreateProcessInput(
                new
                {
                    name = "  caveman  ",
                    baseScore = 4
                },
                new Dictionary<Guid, Dictionary<string, JsonElement>>
                {
                    [blueprint.JoinBlueprintNodeId] = CreateOverrides(
                        ("LeftText", "RUNTIME-LEFT"),
                        ("Mode", "runtime-mode"))
                }),
            cancellationToken);

        var snapshot = await ProcessAssertions.WaitForRealStepsAsync(
            _environment,
            accepted.ProcessId,
            blueprint.BlueprintVersionId,
            new Dictionary<Guid, ProcessStepStatus>
            {
                [blueprint.SeedBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.LeftBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.RightBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.JoinBlueprintNodeId] = ProcessStepStatus.Completed,
            },
            cancellationToken);

        var joinInput = ProcessAssertions.GetInput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.JoinBlueprintNodeId),
            "Request");

        GetPropertyNames(joinInput).ShouldBe(["CombinedScore", "LeftText", "Mode", "RightText"]);
        joinInput.GetProperty("LeftText").GetString().ShouldBe("RUNTIME-LEFT");
        joinInput.GetProperty("RightText").GetString().ShouldBe("RIGHT:21");
        joinInput.GetProperty("CombinedScore").GetInt32().ShouldBe(21);
        joinInput.GetProperty("Mode").GetString().ShouldBe("runtime-mode");
        ProcessAssertions.GetOutput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.JoinBlueprintNodeId),
            "Success").GetProperty("Summary").GetString().ShouldBe("RUNTIME-LEFT|RIGHT:21|21|runtime-mode");
    }

    [Test]
    public async Task Runtime_overrides_take_precedence_over_design_time_overrides(
        CancellationToken cancellationToken)
    {
        var blueprint = await BlueprintScenarioBuilder.CreateOverridePrecedenceScenarioAsync(
            _environment,
            cancellationToken);

        var accepted = await StartProcessAsync(
            blueprint.BlueprintDefinitionId,
            BlueprintScenarioBuilder.CreateProcessInput(
                new
                {
                    name = "  caveman  ",
                    baseScore = 4
                },
                new Dictionary<Guid, Dictionary<string, JsonElement>>
                {
                    [blueprint.JoinBlueprintNodeId] = CreateOverrides(
                        ("LeftText", "RUNTIME-LEFT"))
                }),
            cancellationToken);

        var snapshot = await ProcessAssertions.WaitForRealStepsAsync(
            _environment,
            accepted.ProcessId,
            blueprint.BlueprintVersionId,
            new Dictionary<Guid, ProcessStepStatus>
            {
                [blueprint.SeedBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.LeftBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.RightBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.JoinBlueprintNodeId] = ProcessStepStatus.Completed,
            },
            cancellationToken);

        var joinInput = ProcessAssertions.GetInput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.JoinBlueprintNodeId),
            "Request");

        GetPropertyNames(joinInput).ShouldBe(["CombinedScore", "LeftText", "Mode", "RightText"]);
        joinInput.GetProperty("LeftText").GetString().ShouldBe("RUNTIME-LEFT");
        joinInput.GetProperty("RightText").GetString().ShouldBe("RIGHT:21");
        joinInput.GetProperty("CombinedScore").GetInt32().ShouldBe(21);
        joinInput.GetProperty("Mode").GetString().ShouldBe("design-mode");
        ProcessAssertions.GetOutput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.JoinBlueprintNodeId),
            "Success").GetProperty("Summary").GetString().ShouldBe("RUNTIME-LEFT|RIGHT:21|21|design-mode");
    }

    [Test]
    public async Task Failed_inbound_branch_prevents_join_node_execution(
        CancellationToken cancellationToken)
    {
        var blueprint = await BlueprintScenarioBuilder.CreateFailedBranchJoinScenarioAsync(
            _environment,
            cancellationToken);

        var accepted = await StartProcessAsync(
            blueprint.BlueprintDefinitionId,
            BlueprintScenarioBuilder.CreateProcessInput(
                new
                {
                    name = "  caveman  ",
                    baseScore = 4
                }),
            cancellationToken);

        var snapshot = await ProcessAssertions.WaitForRealStepsAsync(
            _environment,
            accepted.ProcessId,
            blueprint.BlueprintVersionId,
            new Dictionary<Guid, ProcessStepStatus>
            {
                [blueprint.SeedBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.LeftBlueprintNodeId] = ProcessStepStatus.Completed,
                [blueprint.RightBlueprintNodeId] = ProcessStepStatus.Failed,
            },
            cancellationToken);

        ProcessAssertions.AssertNodeDidNotExecute(snapshot, blueprint.JoinBlueprintNodeId);

        var failedStep = ProcessAssertions.GetRealStep(snapshot, blueprint.RightBlueprintNodeId);
        failedStep.ErrorMessage.ShouldNotBeNull();
        failedStep.ErrorMessage.ShouldContain("Requested failure");
        ProcessAssertions.GetOutput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.LeftBlueprintNodeId),
            "Success").GetProperty("Message").GetString().ShouldBe("CAVEMAN:15");
    }

    private static async Task<AcceptedProcess> StartProcessAsync(
        int blueprintDefinitionId,
        BlueprintProcessInputModel processInput,
        CancellationToken cancellationToken)
    {
        var response = await _environment.ApiClient.PostAsJsonAsync(
            $"/api/blueprints/{blueprintDefinitionId}/processes",
            processInput,
            cancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(cancellationToken));

        var accepted = await response.Content.ReadFromJsonAsync<AcceptedProcess>(cancellationToken: cancellationToken);
        accepted.ShouldNotBeNull();
        return accepted;
    }

    private static Dictionary<string, JsonElement> CreateOverrides(
        params (string Key, object? Value)[] entries) =>
        entries.ToDictionary(
            entry => entry.Key,
            entry => JsonSerializer.SerializeToElement(entry.Value, JsonSerializerOptions.Web),
            StringComparer.Ordinal);

    private static string[] GetPropertyNames(JsonElement payload) =>
        payload.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private sealed record AcceptedProcess(Guid ProcessId, int BlueprintId, DateTimeOffset AcceptedAt);
}

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
                    runLabel = "fan-out",
                    stepsCompleted = 0,
                    totalScore = 0,
                    lastStage = "queued",
                    shouldFail = false
                },
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
        GetPropertyNames(leftInput).ShouldBe(["normalizedName", "score"]);
        leftInput.GetProperty("normalizedName").GetString().ShouldBe("CAVEMAN");
        leftInput.GetProperty("score").GetInt32().ShouldBe(5);
        leftOutput.GetProperty("message").GetString().ShouldBe("CAVEMAN:15");
        leftOutput.GetProperty("finalScore").GetInt32().ShouldBe(15);
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
                    runLabel = "join",
                    stepsCompleted = 0,
                    totalScore = 0,
                    lastStage = "queued",
                    shouldFail = false
                },
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

        GetPropertyNames(joinInput).ShouldBe(["combinedScore", "leftText", "rightText"]);
        joinInput.GetProperty("leftText").GetString().ShouldBe("CAVEMAN:15");
        joinInput.GetProperty("rightText").GetString().ShouldBe("RIGHT:21");
        joinInput.GetProperty("combinedScore").GetInt32().ShouldBe(21);

        leftStep.CompletedAt.ShouldNotBeNull();
        rightStep.CompletedAt.ShouldNotBeNull();
        joinStep.CreatedAt.ShouldBeGreaterThanOrEqualTo(leftStep.CompletedAt.Value);
        joinStep.CreatedAt.ShouldBeGreaterThanOrEqualTo(rightStep.CompletedAt.Value);
        ProcessAssertions.GetOutput(joinStep, "Success").GetProperty("summary").GetString().ShouldBe("CAVEMAN:15|RIGHT:21|21|");
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
                    runLabel = "design-time-override",
                    stepsCompleted = 0,
                    totalScore = 0,
                    lastStage = "queued",
                    shouldFail = false
                },
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

        GetPropertyNames(joinInput).ShouldBe(["combinedScore", "leftText", "mode", "rightText"]);
        joinInput.GetProperty("leftText").GetString().ShouldBe("DESIGN-LEFT");
        joinInput.GetProperty("rightText").GetString().ShouldBe("RIGHT:21");
        joinInput.GetProperty("combinedScore").GetInt32().ShouldBe(21);
        joinInput.GetProperty("mode").GetString().ShouldBe("design-mode");
        ProcessAssertions.GetOutput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.JoinBlueprintNodeId),
            "Success").GetProperty("summary").GetString().ShouldBe("DESIGN-LEFT|RIGHT:21|21|design-mode");
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
                    runLabel = "runtime-override",
                    stepsCompleted = 0,
                    totalScore = 0,
                    lastStage = "queued",
                    shouldFail = false
                },
                new
                {
                    name = "  caveman  ",
                    baseScore = 4
                },
                new Dictionary<Guid, Dictionary<string, JsonElement>>
                {
                    [blueprint.JoinBlueprintNodeId] = CreateOverrides(
                        ("leftText", "RUNTIME-LEFT"),
                        ("mode", "runtime-mode"))
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

        GetPropertyNames(joinInput).ShouldBe(["combinedScore", "leftText", "mode", "rightText"]);
        joinInput.GetProperty("leftText").GetString().ShouldBe("RUNTIME-LEFT");
        joinInput.GetProperty("rightText").GetString().ShouldBe("RIGHT:21");
        joinInput.GetProperty("combinedScore").GetInt32().ShouldBe(21);
        joinInput.GetProperty("mode").GetString().ShouldBe("runtime-mode");
        ProcessAssertions.GetOutput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.JoinBlueprintNodeId),
            "Success").GetProperty("summary").GetString().ShouldBe("RUNTIME-LEFT|RIGHT:21|21|runtime-mode");
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
                    runLabel = "override-precedence",
                    stepsCompleted = 0,
                    totalScore = 0,
                    lastStage = "queued",
                    shouldFail = false
                },
                new
                {
                    name = "  caveman  ",
                    baseScore = 4
                },
                new Dictionary<Guid, Dictionary<string, JsonElement>>
                {
                    [blueprint.JoinBlueprintNodeId] = CreateOverrides(
                        ("leftText", "RUNTIME-LEFT"))
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

        GetPropertyNames(joinInput).ShouldBe(["combinedScore", "leftText", "mode", "rightText"]);
        joinInput.GetProperty("leftText").GetString().ShouldBe("RUNTIME-LEFT");
        joinInput.GetProperty("rightText").GetString().ShouldBe("RIGHT:21");
        joinInput.GetProperty("combinedScore").GetInt32().ShouldBe(21);
        joinInput.GetProperty("mode").GetString().ShouldBe("design-mode");
        ProcessAssertions.GetOutput(
            ProcessAssertions.GetRealStep(snapshot, blueprint.JoinBlueprintNodeId),
            "Success").GetProperty("summary").GetString().ShouldBe("RUNTIME-LEFT|RIGHT:21|21|design-mode");
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
                    runLabel = "failed-join-gate",
                    stepsCompleted = 0,
                    totalScore = 0,
                    lastStage = "queued",
                    shouldFail = true
                },
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
            "Success").GetProperty("message").GetString().ShouldBe("CAVEMAN:15");
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

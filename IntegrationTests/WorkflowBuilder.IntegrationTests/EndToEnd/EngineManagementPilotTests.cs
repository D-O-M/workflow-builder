using System.Net.Http.Json;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Services.EngineManagement;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using WorkflowBuilder.IntegrationTests.Infrastructure;
using WorkflowBuilder.IntegrationTests.Scenarios;

namespace WorkflowBuilder.IntegrationTests.EndToEnd;

public sealed class EngineManagementPilotTests
{
    [Test, NotInParallel, Timeout(1_500_000)]
    public async Task Disabling_a_Plugin_parks_the_downstream_Step_and_enabling_it_resumes_the_original_Process(
        CancellationToken cancellationToken)
    {
        var environment = await SharedDistributedAppTestEnvironment.GetReadyAsync(cancellationToken);
        var (blueprint, independentDefinitionId, independentVersionId) =
            await BlueprintScenarioBuilder.CreateManagementPilotBlueprintsAsync(environment, cancellationToken);
        var probeId = Guid.NewGuid();
        var receiptDirectory = Path.Combine(Path.GetTempPath(), "workflow-builder-recovery-tests", probeId.ToString("N"));
        var plugin = (await environment.GetTasksAsync(cancellationToken))
            .Single(task => task.Name == PluginBundleCatalog.TransformPlugin);
        // Integration AppHost disables user authentication. Use the same transactional writer as the API.
        await using var database = await environment.OpenCatalogDatabaseAsync(cancellationToken);
        var policies = new EngineManagementStore(database);
        var original = await policies.ReadAsync(cancellationToken);
        var disabledRevision = await policies.SetEnabledAsync(original.Revision, "integration-operator", plugin.Id,
            versionId: null, enabled: false, cancellationToken);
        long? enabledRevision = null;
        try
        {
            await WaitForApplicationAsync(disabledRevision);
            Directory.CreateDirectory(receiptDirectory);
            var processId = await StartAsync(blueprint.BlueprintDefinitionId, new { probeId, operation = "charge" });
            var waiting = await ProcessAssertions.WaitForSnapshotAsync(environment, processId, blueprint.BlueprintVersionId,
                snapshot => snapshot.RealSteps.Any(step => step.BlueprintNodeId == blueprint.MiddleBlueprintNodeId.ToString()
                                                       && step.PluginWait?.PolicyRevision == disabledRevision),
                cancellationToken);
            var preceding = ProcessAssertions.GetRealStep(waiting, blueprint.FirstBlueprintNodeId);
            var parked = ProcessAssertions.GetRealStep(waiting, blueprint.MiddleBlueprintNodeId);
            preceding.Status.ShouldBe(ProcessStepStatus.Completed);
            parked.Status.ShouldBe(ProcessStepStatus.Pending);
            parked.StartedAt.ShouldBeNull();
            ProcessAssertions.AssertNodeDidNotExecute(waiting, blueprint.FinalBlueprintNodeId);
            var receipt = Directory.GetFiles(receiptDirectory, "charge-*").Single();
            var receiptValue = await File.ReadAllTextAsync(receipt, cancellationToken);
            receiptValue.ShouldNotBeNullOrEmpty();
            var independentProcess = await StartAsync(independentDefinitionId, new { name = "independent", baseScore = 1 });
            await ProcessAssertions.WaitForSuccessfulChainAsync(environment, independentProcess, independentVersionId, 1, cancellationToken);
            var stillWaiting = (await environment.GetProcessStepStateAsync(processId, cancellationToken)).Single(step => step.Id == parked.Id);
            stillWaiting.StartedAt.ShouldBeNull();
            stillWaiting.PluginWait!.ResumedAt.ShouldBeNull();

            if (environment.Settings.EngineReplicas > 1)
            {
                await environment.WithStoppedFleetAsync(async () =>
                {
                    enabledRevision = await EnableAsync();
                    await environment.RunDispatcherJobAsync(cancellationToken);
                    var enqueued = (await environment.GetProcessStepStateAsync(processId, cancellationToken))
                        .Single(step => step.Id == parked.Id);
                    enqueued.Status.ShouldBe(ProcessStepStatus.Pending);
                    enqueued.StartedAt.ShouldBeNull();
                    enqueued.PluginWait!.WakeRequested.ShouldBeTrue();
                    enqueued.DispatchGeneration.ShouldBe(parked.DispatchGeneration + 1);
                    enqueued.Dispatch!.Status.ShouldBe(ProcessStepDispatchStatus.Dispatched);
                    IntegrationProgressLog.Write("[pilot] Verified the one-shot Dispatcher enqueued the original Step with a new generation.");
                }, cancellationToken);
            }
            else
            {
                enabledRevision = await EnableAsync();
            }
            await WaitForApplicationAsync(enabledRevision!.Value);
            var completed = await ProcessAssertions.WaitForSuccessfulChainAsync(environment, processId,
                blueprint.BlueprintVersionId, expectedRealStepCount: 3, cancellationToken);
            var resumed = ProcessAssertions.GetRealStep(completed, blueprint.MiddleBlueprintNodeId);
            resumed.Id.ShouldBe(parked.Id);
            resumed.PluginWait.ShouldNotBeNull();
            resumed.PluginWait.Since.ShouldBe(parked.PluginWait!.Since);
            resumed.PluginWait.ResumedAt.ShouldNotBeNull();
            resumed.DispatchGeneration.ShouldBeGreaterThan(parked.DispatchGeneration);
            var previous = ProcessAssertions.GetRealStep(completed, blueprint.FirstBlueprintNodeId);
            previous.Id.ShouldBe(preceding.Id);
            previous.StartedAt.ShouldBe(preceding.StartedAt);
            previous.CompletedAt.ShouldBe(preceding.CompletedAt);
            Directory.GetFiles(receiptDirectory, "charge-*").ShouldBe([receipt]);
            (await File.ReadAllTextAsync(receipt, cancellationToken)).ShouldBe(receiptValue);
            IntegrationProgressLog.Write("[pilot] Verified the preceding charge produced one receipt before and after the wake-up.");
        }
        finally
        {
            if (Directory.Exists(receiptDirectory))
            {
                foreach (var file in Directory.GetFiles(receiptDirectory, "charge-*")) File.Delete(file);
                Directory.Delete(receiptDirectory);
            }
            if (enabledRevision is null)
                await policies.SetEnabledAsync(disabledRevision, "integration-cleanup", plugin.Id, null, true, CancellationToken.None);
        }

        Task<long> EnableAsync() => policies.SetEnabledAsync(disabledRevision, "integration-operator", plugin.Id,
            versionId: null, enabled: true, cancellationToken);

        async Task<Guid> StartAsync(int definitionId, object initialTaskInput)
        {
            using var response = await environment.ApiClient.PostAsJsonAsync($"/api/blueprints/{definitionId}/processes",
                new { initialTaskInput }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return accepted.GetProperty("processId").GetGuid();
        }

        async Task WaitForApplicationAsync(long revision)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(environment.Settings.ResourceTimeout);
            while (await database.EnginePolicyObservations.AsNoTracking()
                .CountAsync(engine => engine.AppliedRevision == revision && engine.FailureCode == null, timeout.Token)
                < environment.Settings.EngineReplicas)
            {
                await Task.Delay(environment.Settings.PollInterval, timeout.Token);
            }
        }
    }
}

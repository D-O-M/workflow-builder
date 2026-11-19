using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Database.State.Objects;
using Shouldly;
using WorkflowBuilder.IntegrationTests.Infrastructure;
using WorkflowBuilder.IntegrationTests.Scenarios;

namespace WorkflowBuilder.IntegrationTests.EndToEnd;

public sealed class ProcessRecoveryTests
{
    [Test, Timeout(900_000)]
    public async Task Refresh_address_and_deliver_using_the_original_charge(CancellationToken cancellationToken)
    {
        var env = await SharedDistributedAppTestEnvironment.GetReadyAsync(cancellationToken);
        var probe = Guid.NewGuid();
        var directory = Path.Combine(Path.GetTempPath(), "workflow-builder-recovery-tests", probe.ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "address"), "Old address", cancellationToken);
            var blueprint = await BlueprintScenarioBuilder.CreateRecoveryScenarioAsync(env, probe, cancellationToken);
            using var start = await env.ApiClient.PostAsJsonAsync($"/api/blueprints/{blueprint.BlueprintDefinitionId}/processes",
                new { initialTaskInput = new { probeId = probe, operation = "lookup" } }, cancellationToken);
            var processId = (await ReadAsync(start)).GetProperty("processId").GetGuid();
            var failed = await ProcessAssertions.WaitForSnapshotAsync(env, processId, blueprint.BlueprintVersionId,
                snapshot => snapshot.Process.Status == ProcessStatus.Failed, cancellationToken);
            var failedStep = ProcessAssertions.GetRealStep(failed, blueprint.FinalBlueprintNodeId);
            var receipt = await File.ReadAllTextAsync(Directory.GetFiles(directory, "charge-*").Single(), cancellationToken);

            using var preview = await env.ApiClient.PostAsJsonAsync("/api/process-recoveries/preview",
                new { processId, runtimeStepId = failedStep.Id }, cancellationToken);
            var batch = await ReadAsync(preview);
            var path = $"/api/process-recoveries/{batch.GetProperty("id").GetGuid()}";
            using var revised = await env.ApiClient.PutAsJsonAsync($"{path}/preview", new
            {
                revision = batch.GetProperty("revision").GetInt32(),
                edits = new[] { new { processId, blueprintVersionId = blueprint.BlueprintVersionId,
                    blueprintNodeId = blueprint.FirstBlueprintNodeId, continuation = true, action = "Run" } }
            }, cancellationToken);
            var revision = (await ReadAsync(revised)).GetProperty("revision").GetInt32();
            await File.WriteAllTextAsync(Path.Combine(directory, "address"), "New address", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "online"), "", cancellationToken);
            await Task.WhenAll(ConfirmAsync(), ConfirmAsync());
            async Task ConfirmAsync()
            {
                using var confirm = await env.ApiClient.PostAsJsonAsync($"{path}/confirm",
                    new { revision, reason = "Delivery restored. Refresh the address and retain the receipt." }, cancellationToken);
                confirm.StatusCode.ShouldBe(HttpStatusCode.Accepted, await confirm.Content.ReadAsStringAsync(cancellationToken));
            }

            var completed = await ProcessAssertions.WaitForSnapshotAsync(env, processId, blueprint.BlueprintVersionId,
                snapshot => snapshot.Process.Recovery?.Outcome is "Completed" or "Failed" or "Held", cancellationToken);
            completed.Process.Recovery!.Outcome.ShouldBe("Completed", completed.Process.Recovery.Detail);
            completed.Process.Status.ShouldBe(ProcessStatus.Completed);
            Directory.GetFiles(directory, "charge-*").Length.ShouldBe(1);
            var delivery = await File.ReadAllLinesAsync(Directory.GetFiles(directory, "delivery-*").Single(), cancellationToken);
            delivery.ShouldBe(["New address", receipt]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        {
            response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(cancellationToken));
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
    }
}

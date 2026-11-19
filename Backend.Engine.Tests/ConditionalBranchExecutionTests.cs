using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Engine;
using Backend.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed partial class StepFinalizationServiceTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Conditional_step_selects_one_route_and_join_resumes_after_skipped_arm(bool decision)
    {
        var processId = Guid.NewGuid();
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var join = Guid.NewGuid();
        var arms = CreateJoinBlueprint(left, right, join);
        var condition = CreateNode(Guid.NewGuid(), "Input", null, []);
        condition.NodeKind = ExecutableBlueprintNodeKind.ConditionalBranch;
        condition.ConditionalFields = [new() { Name = "urgent", Kind = "Boolean", Required = true }, new() { Name = "rate", Kind = "Decimal" }];
        condition.ConditionalRule = new() { Kind = "comparison", Field = "urgent", Operator = "isTrue" };
        ConnectConditionalOutcome(condition, "True", arms.NodesById[left]);
        ConnectConditionalOutcome(condition, "False", arms.NodesById[right]);
        var blueprint = CreateBlueprint(condition, [condition, .. arms.AllNodes]);
        var steps = new InMemoryProcessStepRepository();
        var process = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var checkpoints = new InMemoryProcessStepFinalizationRepository();
        var writer = new ProcessStepQueueWriterSpy();
        var finalization = CreateService(steps, process, checkpoints, CreateDispatchService(steps, writer));
        var telemetry = new ProcessLifecycleTelemetry(NullLogger<ProcessLifecycleTelemetry>.Instance);
        var claimService = new StepClaimService(steps, process, telemetry);
        var engine = new ConditionalBranchExecutionService(claimService, steps, checkpoints, finalization, telemetry);
        var context = await CreateContextAsync(processId, condition.Id, blueprint, steps, process, claim: false);
        steps.States[context.Id].InputHydrationPlan!.InitialInput["urgent"] = Json(decision);
        using var preciseNumber = JsonDocument.Parse("1.2345678901234567890123456789012e-34");
        steps.States[context.Id].InputHydrationPlan!.InitialInput["rate"] = preciseNumber.RootElement.Clone();
        // Simulate redelivery after the original worker stopped between claim and checkpoint.
        if (decision)
            (await claimService.TryClaimStepAsync(context, CancellationToken.None)).StepClaimed.ShouldBeTrue();

        await engine.ExecuteAsync(context, CancellationToken.None);

        var selected = decision ? left : right;
        var skipped = decision ? right : left;
        var outcome = decision ? "True" : "False";
        steps.States[context.Id].OutputData!.Single().Key.ShouldBe(outcome);
        steps.States[context.Id].OutputData![outcome].GetProperty("urgent").GetBoolean().ShouldBe(decision);
        steps.States[context.Id].OutputData![outcome].GetProperty("rate").GetRawText().ShouldBe(preciseNumber.RootElement.GetRawText());
        writer.Messages.Select(m => m.BlueprintNodeId).ShouldBe([selected]);
        var skippedStep = steps.States[ProcessStepIdentity.Create(processId, skipped)];
        skippedStep.Status.ShouldBe(ProcessStepStatus.Skipped);
        skippedStep.StartedAt.ShouldBeNull();
        skippedStep.Dispatch.ShouldBeNull();
        var checkpoint = checkpoints.Records[context.Id];
        checkpoint.Source.ShouldBe(ProcessStepFinalizationSource.ConditionalBranch);
        checkpoint.Stage = ProcessStepFinalizationStage.OutputsPersisted;
        condition.ConditionalRule.Negated = true;
        await finalization.ResumeStepFinalizationForRecoveryAsync(context, checkpoint, CancellationToken.None);
        process.State.ExecutionProgress.SkippedStepIds.ShouldBe([skippedStep.Id]);
        writer.Messages.Count.ShouldBe(1);
        process.State.Status.ShouldBe(ProcessStatus.InProgress);

        var selectedContext = await CreateContextAsync(processId, selected, blueprint, steps, process);
        await finalization.FinalizeSucceededStepAsync(selectedContext, CreateSuccessResult(selectedContext, "Out"), CancellationToken.None);
        writer.Messages.Select(m => m.BlueprintNodeId).ShouldBe([selected, join]);
        var joinContext = await CreateContextAsync(processId, join, blueprint, steps, process);
        joinContext.InputHydrationPlan!.Sources.Single().SourceProcessStepId.ShouldBe(selectedContext.Id.ToString());
        await finalization.FinalizeSucceededStepAsync(joinContext, CreateSuccessResult(joinContext, null), CancellationToken.None);
        process.State.Status.ShouldBe(ProcessStatus.Completed);
        process.State.ExecutionProgress.CompletedStepIds.Count.ShouldBe(3);
    }

    [Test]
    public async Task Iterator_items_choose_independent_routes_and_an_unconnected_outcome_completes()
    {
        var processId = Guid.NewGuid();
        var iteratorId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var graph = CreateCollectionIteratorBlueprint(iteratorId, conditionId);
        var condition = graph.NodesById[conditionId];
        condition.NodeKind = ExecutableBlueprintNodeKind.ConditionalBranch;
        condition.ConditionalFields = [new() { Name = "Text", Kind = "String", Required = true }];
        condition.ConditionalRule = new() { Kind = "comparison", Field = "Text", Operator = "eq", Operands = [new() { Literal = Json("accept") }] };
        var child = CreateNode(Guid.NewGuid(), "Input", null, []);
        ConnectConditionalOutcome(condition, "True", child);
        condition.OutputChannels["False"] = condition.OutputChannels["True"] with { Name = "False", OutboundBindings = [] };
        var blueprint = CreateBlueprint(graph.InitialNode, [.. graph.AllNodes, child]);
        var steps = new InMemoryProcessStepRepository();
        var process = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var checkpoints = new InMemoryProcessStepFinalizationRepository();
        var writer = new ProcessStepQueueWriterSpy();
        var finalization = CreateService(steps, process, checkpoints, CreateDispatchService(steps, writer));
        var telemetry = new ProcessLifecycleTelemetry(NullLogger<ProcessLifecycleTelemetry>.Instance);
        var claimService = new StepClaimService(steps, process, telemetry);
        var engine = new ConditionalBranchExecutionService(claimService, steps, checkpoints, finalization, telemetry);
        var iterator = await CreateContextAsync(processId, iteratorId, blueprint, steps, process);
        await finalization.FinalizeSucceededCollectionIteratorStepAsync(iterator, CreateCollectionIteratorResult(iterator, ["accept", "deny"]),
            repeatedBlueprintNodeCount: 2, itemCount: 2, batchSize: 2, CancellationToken.None);

        foreach (var message in writer.Messages.ToArray())
        {
            var item = new TestStepExecutionContext(message, new ProcessStepQueueMessageSpy(message), blueprint);
            await engine.ExecuteAsync(item, CancellationToken.None);
        }

        var selected = writer.Messages.Single(m => m.BlueprintNodeId == child.Id);
        steps.States[selected.ProcessStepId].IterationContext!.ItemIndex.ShouldBe(0);
        steps.States.Values.Single(s => s.Status == ProcessStepStatus.Skipped).IterationContext!.ItemIndex.ShouldBe(1);
        var active = new TestStepExecutionContext(selected, new ProcessStepQueueMessageSpy(selected), blueprint);
        (await claimService.TryClaimStepAsync(active, CancellationToken.None)).StepClaimed.ShouldBeTrue();
        await finalization.FinalizeSucceededStepAsync(active, CreateSuccessResult(active, null), CancellationToken.None);
        process.State.Status.ShouldBe(ProcessStatus.Completed);
        process.State.ExecutionProgress.TotalStepCount.ShouldBe(5);
        process.State.ExecutionProgress.SkippedStepIds.Count.ShouldBe(1);
    }

    [Test]
    public async Task Missing_compared_value_records_conditional_failure_without_dispatch()
    {
        var processId = Guid.NewGuid();
        var condition = CreateNode(Guid.NewGuid(), "Input", null, []);
        condition.NodeKind = ExecutableBlueprintNodeKind.ConditionalBranch;
        condition.ConditionalFields = [new() { Name = "due", Kind = "Date" }];
        condition.ConditionalRule = new()
        {
            Kind = "comparison", Field = "due", Operator = "eq", Operands = [new() { Literal = Json("2026-11-06") }]
        };
        var child = CreateNode(Guid.NewGuid(), "Input", null, []);
        ConnectConditionalOutcome(condition, "False", child);
        var blueprint = CreateBlueprint(condition, [condition, child]);
        var steps = new InMemoryProcessStepRepository();
        var process = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var checkpoints = new InMemoryProcessStepFinalizationRepository();
        var writer = new ProcessStepQueueWriterSpy();
        var finalization = CreateService(steps, process, checkpoints, CreateDispatchService(steps, writer));
        var telemetry = new ProcessLifecycleTelemetry(NullLogger<ProcessLifecycleTelemetry>.Instance);
        var engine = new ConditionalBranchExecutionService(new StepClaimService(steps, process, telemetry),
            steps, checkpoints, finalization, telemetry);
        var context = await CreateContextAsync(processId, condition.Id, blueprint, steps, process, claim: false);
        await engine.ExecuteAsync(context, CancellationToken.None);
        process.State.Status.ShouldBe(ProcessStatus.Failed);
        steps.States[context.Id].Status.ShouldBe(ProcessStepStatus.Failed);
        steps.States[context.Id].ErrorMessage!.ShouldContain("due");
        checkpoints.Records[context.Id].Source.ShouldBe(ProcessStepFinalizationSource.ConditionalBranch);
        writer.Messages.ShouldBeEmpty();
        steps.CreatedSteps.ShouldBeEmpty();
    }

    [Test]
    public async Task Entire_inactive_diamond_skips_shared_descendants_once_and_completes()
    {
        var processId = Guid.NewGuid();
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var join = Guid.NewGuid();
        var arms = CreateJoinBlueprint(left, right, join);
        var condition = CreateNode(Guid.NewGuid(), "Input", null, []);
        condition.NodeKind = ExecutableBlueprintNodeKind.ConditionalBranch;
        ConnectConditionalOutcome(condition, "False", arms.NodesById[left]);
        var rightBinding = condition.OutputChannels["False"].OutboundBindings.Single() with { Id = "False-right", DestinationBlueprintNodeId = right };
        condition.OutputChannels["False"].OutboundBindings.Add(rightBinding);
        arms.NodesById[right].InboundBindings.Add(rightBinding);
        var tail = CreateNode(Guid.NewGuid(), "Input", null, []);
        ConnectConditionalOutcome(arms.NodesById[join], "Out", tail);
        var blueprint = CreateBlueprint(condition, [condition, .. arms.AllNodes, tail]);
        var steps = new InMemoryProcessStepRepository();
        var process = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var checkpoints = new InMemoryProcessStepFinalizationRepository();
        var writer = new ProcessStepQueueWriterSpy();
        var service = CreateService(steps, process, checkpoints, CreateDispatchService(steps, writer));
        var context = await CreateContextAsync(processId, condition.Id, blueprint, steps, process);

        await service.FinalizeSucceededStepAsync(context, CreateSuccessResult(context, "True"), CancellationToken.None);

        process.State.Status.ShouldBe(ProcessStatus.Completed);
        steps.CreatedSteps.Count.ShouldBe(4);
        steps.CreatedSteps.ShouldAllBe(step => step.Status == ProcessStepStatus.Skipped && step.Dispatch == null);
        process.State.ExecutionProgress.SkippedStepIds.Count.ShouldBe(4);
        writer.Messages.ShouldBeEmpty();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Both_outcomes_to_same_join_wait_for_parallel_prerequisite_and_dispatch_once_after_retry(bool prerequisiteFirst)
    {
        var processId = Guid.NewGuid();
        var prerequisite = CreateNode(Guid.NewGuid(), "OtherInput", null, []);
        var condition = CreateNode(Guid.NewGuid(), "Input", null, []);
        condition.NodeKind = ExecutableBlueprintNodeKind.ConditionalBranch;
        var join = CreateNode(Guid.NewGuid(), "JoinInput", null, []);
        ConnectConditionalOutcome(condition, "True", join);
        ConnectConditionalOutcome(condition, "False", join);
        ConnectConditionalOutcome(prerequisite, "Other", join);
        var blueprint = CreateBlueprint(condition, [condition, prerequisite, join]);
        var steps = new InMemoryProcessStepRepository();
        var process = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var checkpoints = new InMemoryProcessStepFinalizationRepository();
        var joins = new InMemoryNodeJoinStateRepository();
        var writer = new ProcessStepQueueWriterSpy();
        var service = CreateService(steps, process, checkpoints, CreateDispatchService(steps, writer), nodeJoinStateRepository: joins);
        var conditionContext = await CreateContextAsync(processId, condition.Id, blueprint, steps, process);
        var prerequisiteContext = await CreateContextAsync(processId, prerequisite.Id, blueprint, steps, process);
        var first = prerequisiteFirst ? prerequisiteContext : conditionContext;
        var last = prerequisiteFirst ? conditionContext : prerequisiteContext;
        await service.FinalizeSucceededStepAsync(first, CreateSuccessResult(first, prerequisiteFirst ? "Other" : "True"), CancellationToken.None);
        steps.CreatedSteps.ShouldBeEmpty();
        joins.FailMaterializationUpdateAttempts = 10;
        await Should.ThrowAsync<InvalidOperationException>(() =>
            service.FinalizeSucceededStepAsync(last, CreateSuccessResult(last, prerequisiteFirst ? "True" : "Other"), CancellationToken.None));
        writer.Messages.ShouldBeEmpty();
        var child = steps.CreatedSteps.Single();
        child.InputHydrationPlan!.Sources.Select(source => source.BindingId).ShouldBe(["Other", "True"]);
        joins.FailMaterializationUpdateAttempts = 0;
        var record = checkpoints.Records[last.Id];
        record.Stage.ShouldBe(ProcessStepFinalizationStage.OutputsPersisted);
        await service.ResumeStepFinalizationForRecoveryAsync(last, record, CancellationToken.None);
        record.Stage = ProcessStepFinalizationStage.OutputsPersisted;
        await service.ResumeStepFinalizationForRecoveryAsync(last, record, CancellationToken.None);
        steps.CreatedSteps.Count.ShouldBe(1);
        writer.Messages.Single().BlueprintNodeId.ShouldBe(join.Id);
        joins.States[(processId, join.Id)].InactiveBindingIds.ShouldBe(["False"]);
        process.State.ExecutionProgress.SkippedStepIds.ShouldBeEmpty();
    }

    private static void ConnectConditionalOutcome(ExecutableBlueprintNode source, string outcome, ExecutableBlueprintNode target)
    {
        var binding = new ExecutableBlueprintBinding
        {
            Id = outcome,
            SourceBlueprintNodeId = source.Id,
            SourceChannelName = outcome,
            DestinationBlueprintNodeId = target.Id,
            PropertyMappings = []
        };
        source.OutputChannels[outcome] = new() { Name = outcome, Properties = [], OutboundBindings = [binding] };
        target.InboundBindings.Add(binding);
    }
}

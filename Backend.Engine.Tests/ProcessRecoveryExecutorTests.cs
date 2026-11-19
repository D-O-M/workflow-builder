using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Dispatcher;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Services.ProcessStepQueue;
using Backend.Services.Recovery;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class ProcessRecoveryExecutorTests
{
    [Test]
    public async Task Refresh_and_reuse_bind_a_join_to_the_reviewed_attempts_even_after_a_dispatch_interruption()
    {
        var f = new Fixture();
        var address = f.Add("address", "Run", new { address = "old" });
        var charge = f.Add("charge", "Reuse", new { receipt = "paid" });
        var deliver = f.Add("deliver", "Run", failed: true);
        f.Connect(address, deliver, "address");
        f.Connect(charge, deliver, "receipt");
        f.FailFirstSend = true;

        var result = await f.FinishAsync(step =>
        {
            if (step.BlueprintNodeId == address.ToString()) step.OutputData = Output(new { address = "fresh" });
            if (step.BlueprintNodeId == deliver.ToString())
            {
                var inputs = step.InputHydrationPlan!.Sources.Select(s => f.History.Single(h => h.Id == s.SourceProcessStepId)).ToList();
                inputs.Single(s => s.BlueprintNodeId == address.ToString()).OutputData!["out"].GetProperty("address").GetString().ShouldBe("fresh");
                inputs.Single(s => s.BlueprintNodeId == charge.ToString()).OutputData!["out"].GetProperty("receipt").GetString().ShouldBe("paid");
            }
        });

        result.State.ShouldBe("Completed");
        f.Process.Status.ShouldBe(ProcessStatus.Completed);
        (await f.AdvanceAsync()).State.ShouldBe("Completed");
        f.Executed.Count.ShouldBe(2);
        f.Executed.ShouldNotContain(s => s.BlueprintNodeId == charge.ToString());
        f.History.Count(s => s.RecoveryAction == "Reuse").ShouldBe(1);
    }

    [Test]
    public async Task Skipping_an_uncertain_charge_holds_only_the_consumer_missing_a_receipt()
    {
        var f = new Fixture();
        var charge = f.Add("charge", "Skip", failed: true);
        var receipt = f.Add("receipt", "Run", failed: true);
        var independent = f.Add("address check", "Run", failed: true);
        f.Connect(charge, receipt, "paymentReference");
        f.Connect(charge, independent, null);

        var result = await f.FinishAsync();
        result.State.ShouldBe("Held");
        result.Detail.ShouldNotBeNull().ShouldContain("paymentReference");
        f.Executed.Select(s => s.BlueprintNodeId).ShouldBe([independent.ToString()]);
        f.Effective.Single(s => s.BlueprintNodeId == charge.ToString()).Status.ShouldBe(ProcessStepStatus.Skipped);
        f.Effective.Single(s => s.BlueprintNodeId == receipt.ToString()).RecoveryAction.ShouldBe("Review");
        f.Process.Status.ShouldBe(ProcessStatus.Failed);
    }

    [Test]
    public async Task A_null_override_masks_a_recorded_reference_and_holds_the_dependent_Step()
    {
        var f = new Fixture();
        var charge = f.Add("payment", "Reuse", new { reference = "provider-reference" });
        var receipt = f.Add("receipt", "Run", failed: true);
        f.Connect(charge, receipt, "reference");
        f.Process.NodeOverrides[receipt.ToString()] = new() { ["reference"] = JsonSerializer.SerializeToElement<string?>(null) };
        var result = await f.FinishAsync();
        result.State.ShouldBe("Held");
        result.Detail.ShouldNotBeNull().ShouldContain("reference");
        f.Executed.ShouldBeEmpty();
    }

    [Test]
    [Arguments("Pause", "Held")]
    [Arguments("Run", "Completed")]
    public async Task A_changed_conditional_branch_obeys_the_frozen_new_work_policy(string policy, string outcome)
    {
        var f = new Fixture();
        var branch = f.Add("decision", "Run", failed: true, kind: ExecutableBlueprintNodeKind.ConditionalBranch);
        var destination = f.Add("new route", "Run", recorded: false);
        f.Connect(branch, destination, null);
        f.PreviewUnreached(destination);
        var inactive = f.Add("other route", "Run", recorded: false);
        f.Connect(branch, inactive, null, "other");
        f.PreviewUnreached(inactive);
        f.Plan.NewWorkPolicy = policy;

        var result = await f.FinishAsync(step => step.OutputData = Output(new { value = 1 }));
        result.State.ShouldBe(outcome);
        f.Executed.Count(s => s.BlueprintNodeId == destination.ToString()).ShouldBe(policy == "Run" ? 1 : 0);
        f.History.ShouldNotContain(s => s.BlueprintNodeId == inactive.ToString());
    }

    [Test]
    [Arguments(false, "Pause", "Held")]
    [Arguments(false, "Run", "Completed")]
    [Arguments(true, "Pause", "Completed")]
    public async Task A_new_Plugin_output_channel_uses_the_new_work_policy_only_after_a_recorded_success(
        bool firstSuccess, string policy, string expected)
    {
        var f = new Fixture();
        var source = f.Add("route order", "Run", firstSuccess ? null : new { value = "initial" }, failed: firstSuccess);
        var oldPath = f.Add("initial path", "Run", failed: !firstSuccess, recorded: !firstSuccess);
        var newPath = f.Add("different payment provider", "Run", recorded: false);
        f.Connect(source, oldPath, null);
        f.Connect(source, newPath, null, "alternative");
        f.PreviewUnreached(newPath);
        f.Plan.NewWorkPolicy = policy;

        var result = await f.FinishAsync(step =>
        {
            if (step.BlueprintNodeId == source.ToString())
                step.OutputData = new() { ["alternative"] = JsonSerializer.SerializeToElement(new { value = "refreshed" }) };
        });

        result.State.ShouldBe(expected);
        f.Executed.ShouldNotContain(s => s.BlueprintNodeId == oldPath.ToString());
        f.Executed.Count(s => s.BlueprintNodeId == newPath.ToString()).ShouldBe(expected == "Held" ? 0 : 1);
        if (expected == "Held")
            f.Effective.Single(s => s.BlueprintNodeId == newPath.ToString()).RecoveryAction.ShouldBe("Review");
    }

    [Test]
    public async Task Independent_failed_siblings_can_recover_separately_and_the_join_uses_both_effective_attempts()
    {
        var f = new Fixture();
        var left = f.Add("left", "Run", failed: true);
        var right = f.Add("right", "Run", failed: true);
        var join = f.Add("join", "Run", recorded: false);
        f.Connect(left, join, "a");
        f.Connect(right, join, "b");
        var first = f.Plan.Steps.Single(s => s.BlueprintNodeId == left);
        var second = f.Plan.Steps.Single(s => s.BlueprintNodeId == right);
        second.InScope = false;
        f.Plan.TargetStepIds = [first.StepId];
        f.PreviewUnreached(join);

        var result = await f.FinishAsync(s => s.OutputData = Output(new { a = 1 }));
        result.State.ShouldBe("Completed");
        f.Process.Status.ShouldBe(ProcessStatus.Failed);
        f.Executed.Count.ShouldBe(1);

        f.NextRevision();
        first.StepId = Guid.Parse(f.Effective.Single(s => s.BlueprintNodeId == left.ToString()).Id);
        first.InScope = false;
        second.InScope = true;
        f.Plan.TargetStepIds = [second.StepId];
        result = await f.FinishAsync(s => s.OutputData = Output(new { b = 1 }));

        result.State.ShouldBe("Completed");
        f.Process.Status.ShouldBe(ProcessStatus.Completed);
        f.Executed.Count(s => s.BlueprintNodeId == left.ToString()).ShouldBe(1);
        var sources = f.Executed.Single(s => s.BlueprintNodeId == join.ToString()).InputHydrationPlan!.Sources;
        sources.Count.ShouldBe(2);
        sources.ShouldAllBe(source => f.Executed.Any(s => s.Id == source.SourceProcessStepId));
    }

    [Test]
    public async Task Reviewing_a_new_branch_resumes_its_held_occurrence_without_reexecuting_the_decision()
    {
        var f = new Fixture();
        var branch = f.Add("decision", "Run", failed: true, kind: ExecutableBlueprintNodeKind.ConditionalBranch);
        var destination = f.Add("new route", "Run", recorded: false);
        f.Connect(branch, destination, null);
        (await f.FinishAsync(step => step.OutputData = Output(new { value = 1 }))).State.ShouldBe("Held");
        var review = f.Effective.Single(s => s.RecoveryAction == "Review");

        f.Review(Guid.Parse(review.Id));
        (await f.FinishAsync()).State.ShouldBe("Completed");
        f.Executed.Count(s => s.BlueprintNodeId == branch.ToString()).ShouldBe(1);
        f.Executed.Count(s => s.BlueprintNodeId == destination.ToString()).ShouldBe(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Reordered_items_never_reuse_old_receipts_by_position_even_with_automatic_new_work(bool inserted)
    {
        var f = new Fixture();
        var iterator = f.Add("orders", "Run", kind: ExecutableBlueprintNodeKind.CollectionIterator);
        var pay = f.Add("payment", "Reuse", recorded: false);
        f.Connect(iterator, pay, "item", "Item");
        var iteratorStep = f.History.Single();
        iteratorStep.InputData = new() { ["input"] = JsonSerializer.SerializeToElement(new { items = new[] { "A", "B" } }) };
        for (var i = 0; i < 2; i++)
        {
            var iteration = new ProcessStepIterationContext { SourceIteratorStepId = iteratorStep.Id,
                SourceIteratorBlueprintNodeId = iterator.ToString(), ItemIndex = i, ItemKey = $"{Guid.Parse(iteratorStep.Id):N}:{i:D8}" };
            f.Record(pay, "Reuse", i == 1, iteration, Output(new { receipt = i }));
        }
        f.Plan.NewWorkPolicy = "Run";
        var result = await f.FinishAsync(step => step.InputData = new()
        {
            ["input"] = JsonSerializer.SerializeToElement(new { items = inserted ? new[] { "X", "A", "B" } : new[] { "B", "A" } })
        });
        result.State.ShouldBe("Held");
        f.Executed.Count.ShouldBe(1);
        f.Effective.Count(s => s.BlueprintNodeId == pay.ToString() && s.RecoveryAction == "Review").ShouldBe(inserted ? 3 : 2);
        f.Effective.Where(s => s.BlueprintNodeId == pay.ToString()).ShouldAllBe(s => s.OutputData == null);
    }

    private static Dictionary<string, JsonElement> Output(object data) =>
        new() { ["out"] = JsonSerializer.SerializeToElement(data) };

    private sealed class Fixture
    {
        public ProcessInstanceRecord Process { get; } = new() { id = Guid.NewGuid().ToString(), BlueprintId = "1", Status = ProcessStatus.Failed };
        public RecoveryCasePlan Plan { get; private set; } = new() { BlueprintVersionId = 1 };
        public List<ProcessStepRecord> History { get; } = [];
        public List<ProcessStepRecord> Executed { get; } = [];
        public bool FailFirstSend { get; set; }
        public IReadOnlyList<ProcessStepRecord> Effective => ProcessRecoveryState.EffectiveSteps(Process, History);
        private readonly ExecutableBlueprint _blueprint = new() { Id = 1, AllNodes = [], InitialNode = null! };
        private readonly Guid _recoveryId = Guid.NewGuid();
        private int _revision = 1;
        private readonly ProcessRecoveryExecutor _executor;

        public Fixture()
        {
            Plan.ProcessId = Guid.Parse(Process.Id);
            var processRepository = new Mock<IProcessInstanceRepository>();
            processRepository.Setup(r => r.GetProcessStateById(Plan.ProcessId, 1, It.IsAny<CancellationToken>())).ReturnsAsync(Process);
            processRepository.Setup(r => r.TryUpdateProcessState(Process, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var stepRepository = new Mock<IProcessStepRepository>();
            stepRepository.Setup(r => r.GetProcessStepsForProcess(Plan.ProcessId, It.IsAny<CancellationToken>())).ReturnsAsync(() => History.ToList());
            stepRepository.Setup(r => r.CreateProcessStepsIfNotExists(It.IsAny<IReadOnlyCollection<ProcessStepRecord>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<ProcessStepRecord> requested, CancellationToken _) => requested.Select(s =>
                {
                    var current = History.FirstOrDefault(h => h.Id == s.Id);
                    if (current != null) return (false, current);
                    History.Add(s);
                    return (true, s);
                }).ToList());
            var cache = new Mock<IBlueprintExecutionCache>();
            cache.Setup(c => c.GetExecutableBlueprintAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(_blueprint);
            var dispatcher = new Mock<IProcessStepDispatcher>();
            dispatcher.Setup(d => d.TryDispatchAsync(It.IsAny<ProcessStepRecord>(), It.IsAny<ProcessStepDispatchIntentRecord>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProcessStepRecord pending, ProcessStepDispatchIntentRecord _, CancellationToken _) =>
                {
                    if (FailFirstSend)
                    {
                        FailFirstSend = false;
                        throw new TimeoutException("Interrupt dispatch after the physical attempt was persisted.");
                    }
                    pending.Status = ProcessStepStatus.InProgress;
                    Executed.Add(pending);
                    return true;
                });
            _executor = new(processRepository.Object, stepRepository.Object, Mock.Of<IProcessStepFinalizationRepository>(),
                cache.Object, dispatcher.Object, null!, Mock.Of<IProcessIndexProjectionService>(), Mock.Of<IProcessRealtimeProjectionService>());
        }

        public Guid Add(string name, string action, object? output = null, bool failed = false, bool recorded = true,
            ExecutableBlueprintNodeKind kind = ExecutableBlueprintNodeKind.PluginTask)
        {
            var node = new ExecutableBlueprintNode { Id = Guid.NewGuid(), Name = name, NodeKind = kind, ResolvedVersion = new(1, 0),
                InputChannel = new() { Name = "input", Properties = [], OutboundBindings = [] },
                OutputChannels = [], InboundBindings = [], DesignTimeInputOverrides = [] };
            if (_blueprint.AllNodes.Count == 0) _blueprint.InitialNode = node;
            _blueprint.AllNodes.Add(node);
            if (recorded) Record(node.Id, action, failed, null, output == null ? null : Output(output));
            return node.Id;
        }

        public void Record(Guid nodeId, string action, bool failed, ProcessStepIterationContext? iteration,
            Dictionary<string, JsonElement>? outputs)
        {
            var id = ProcessStepIdentity.Create(Plan.ProcessId, nodeId, iteration?.ItemKey);
            var step = new ProcessStepRecord { Id = id.ToString(), ProcessId = Process.Id, BlueprintId = "1",
                BlueprintNodeId = nodeId.ToString(), OutputData = outputs, IterationContext = iteration,
                Status = failed ? ProcessStepStatus.Failed : ProcessStepStatus.Completed };
            History.Add(step);
            if (failed) Plan.TargetStepIds.Add(id);
            Plan.Steps.Add(new() { StepId = id, LogicalStepId = id, BlueprintNodeId = nodeId, Status = step.Status.ToString(),
                Action = action, InScope = true, ItemKey = iteration?.ItemKey });
        }

        public void Connect(Guid source, Guid destination, string? property, string output = "out")
        {
            var from = _blueprint.AllNodes.Single(n => n.Id == source);
            var to = _blueprint.AllNodes.Single(n => n.Id == destination);
            var binding = new ExecutableBlueprintBinding { Id = Guid.NewGuid().ToString(), SourceBlueprintNodeId = source,
                DestinationBlueprintNodeId = destination, SourceChannelName = output, PropertyMappings = [] };
            if (property != null)
            {
                to.InputChannel.RequiredProperties.Add(property);
                binding.PropertyMappings.Add(new() { SourceProperty = property, TargetProperty = property });
            }
            to.InboundBindings.Add(binding);
            if (!from.OutputChannels.ContainsKey(output))
                from.OutputChannels[output] = new() { Name = output, Properties = [], OutboundBindings = [] };
            from.OutputChannels[output].OutboundBindings.Add(binding);
        }

        public void PreviewUnreached(Guid node)
        {
            var id = ProcessStepIdentity.Create(Plan.ProcessId, node);
            Plan.Steps.Add(new() { StepId = id, LogicalStepId = id, BlueprintNodeId = node,
                Status = "NotExecuted", Action = "Run", InScope = true });
        }

        public void NextRevision() => _revision++;
        public void Review(Guid target)
        {
            NextRevision();
            Plan = ProcessRecoveryPlanner.Build(_blueprint, Process, History, [target]);
        }

        public Task<RecoveryExecutionResult> AdvanceAsync() => _executor.AdvanceAsync(_recoveryId, _revision, Plan, CancellationToken.None);

        public async Task<RecoveryExecutionResult> FinishAsync(Action<ProcessStepRecord>? onExecution = null)
        {
            for (var i = 0; i < 20; i++)
            {
                RecoveryExecutionResult result;
                try { result = await AdvanceAsync(); }
                catch (TimeoutException) { continue; }
                if (result.State != "Running") return result;
                foreach (var step in Executed.Where(s => s.Status == ProcessStepStatus.InProgress))
                {
                    onExecution?.Invoke(step);
                    step.Status = ProcessStepStatus.Completed;
                }
            }
            throw new InvalidOperationException("Recovery did not settle.");
        }
    }
}

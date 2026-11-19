using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Models;
using Backend.Services.Recovery;
using Shouldly;

namespace Backend.API.Tests;

public sealed class ProcessRecoveryPlannerTests
{
    private readonly ProcessInstanceRecord process = new() { id = Guid.NewGuid().ToString(), BlueprintId = "7", Status = ProcessStatus.Failed };
    private readonly ExecutableBlueprintNode lookup = Node("Address");
    private readonly ExecutableBlueprintNode payment = Node("Payment");
    private readonly ExecutableBlueprintNode delivery = Node("Delivery");

    [Test]
    public void Refresh_preserves_payment_and_accepts_only_the_same_occurrences_receipt()
    {
        var address = Occurrence(lookup, ProcessStepStatus.Completed, "Address", "old");
        var charge = Occurrence(payment, ProcessStepStatus.Completed, "Receipt", "R-1", address);
        var receipt = Occurrence(delivery, ProcessStepStatus.Failed, null, null, address, charge);
        var target = Guid.Parse(receipt.Id);
        var anchor = Guid.Parse(address.Id);
        var chargeId = Guid.Parse(charge.Id);
        var graph = Graph();
        var steps = new[] { address, charge, receipt };

        var plan = ProcessRecoveryPlanner.Build(graph, process, steps, [target], [anchor], new Dictionary<Guid,string> { [anchor] = "Run" });
        var reused = plan.Steps.Single(s => s.StepId == chargeId);
        reused.Action.ShouldBe("Reuse");
        reused.InScope.ShouldBeTrue();
        reused.ReuseStepId.ShouldBe(chargeId);
        reused.Outputs!["Success"].GetProperty("Receipt").GetString().ShouldBe("R-1");
        plan.Steps.Single(s => s.StepId == target).MissingInputs.ShouldBeEmpty();

        var run = ProcessRecoveryPlanner.Build(graph, process, steps, [target], [anchor], new Dictionary<Guid,string> { [chargeId] = "Run" });
        run.Steps.Single(s => s.StepId == chargeId).ActionExplicit.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => ProcessRecoveryPlanner.Build(graph, process, steps, [target], [anchor],
            reuseSources: new Dictionary<Guid,Guid> { [chargeId] = anchor }));
    }

    [Test]
    public void Skipping_payment_names_missing_receipt_and_accepts_an_existing_override()
    {
        var address = Occurrence(lookup, ProcessStepStatus.Completed, "Address", "old");
        var charge = Occurrence(payment, ProcessStepStatus.Failed, null, null, address);
        var receipt = Occurrence(delivery, ProcessStepStatus.Failed, null, null, address, charge);
        var actions = new Dictionary<Guid,string> { [Guid.Parse(charge.Id)] = "Skip" };
        var graph = Graph();
        RecoveryPlanStep Delivery() => ProcessRecoveryPlanner.Build(graph, process, [address, charge, receipt],
            [Guid.Parse(charge.Id), Guid.Parse(receipt.Id)], actions: actions).Steps.Single(s => s.StepId == Guid.Parse(receipt.Id));
        Delivery().MissingInputs.ShouldBe(["Receipt"]);
        receipt.InputHydrationPlan!.RuntimeInputOverrides["Receipt"] = JsonSerializer.SerializeToElement("existing override");
        Delivery().MissingInputs.ShouldBeEmpty();
        receipt.InputHydrationPlan.RuntimeInputOverrides["Receipt"] = JsonSerializer.SerializeToElement<string?>(null);
        Delivery().MissingInputs.ShouldBe(["Receipt"]);
    }

    [Test]
    public void A_blocked_downstream_Step_is_visible_before_it_has_ever_executed()
    {
        var graph = Graph();
        var charge = Occurrence(payment, ProcessStepStatus.Failed, null, null);
        var chargeId = Guid.Parse(charge.Id);
        var binding = new ExecutableBlueprintBinding
        {
            Id = "receipt", SourceBlueprintNodeId = payment.Id, DestinationBlueprintNodeId = delivery.Id, SourceChannelName = "Success",
            PropertyMappings = [new() { SourceProperty = "Receipt", TargetProperty = "Receipt" }]
        };
        payment.OutputChannels["Success"] = new() { Name = "Success", Properties = [], OutboundBindings = [binding] };
        delivery.InboundBindings.Add(binding);
        delivery.DesignTimeInputOverrides["Address"] = JsonSerializer.SerializeToElement("recorded address");
        var edits = new Dictionary<Guid, string> { [chargeId] = "Skip" };
        var plan = ProcessRecoveryPlanner.Build(graph, process, [charge], [chargeId], actions: edits);
        var future = plan.Steps.Single(s => s.BlueprintNodeId == delivery.Id);
        future.Status.ShouldBe("NotExecuted");
        future.MissingInputs.ShouldBe(["Receipt"]);
        edits[future.StepId] = "Skip";
        ProcessRecoveryPlanner.Build(graph, process, [charge], [chargeId], actions: edits)
            .Steps.Single(s => s.BlueprintNodeId == delivery.Id).Action.ShouldBe("Skip");
    }

    [Test]
    public void Retry_one_item_leaves_an_unselected_failed_occurrence_outside_the_plan()
    {
        var address = Occurrence(lookup, ProcessStepStatus.Completed, "Address", "one");
        var failed = Occurrence(delivery, ProcessStepStatus.Failed, null, null, address);
        var sibling = Occurrence(delivery, ProcessStepStatus.Failed, null, null, address);
        var plan = ProcessRecoveryPlanner.Build(Graph(), process, [address, failed, sibling],
            [Guid.Parse(failed.Id)], [Guid.Parse(address.Id)]);
        plan.Steps.Single(s => s.StepId == Guid.Parse(sibling.Id)).InScope.ShouldBeFalse();
        plan.Steps.Single(s => s.StepId == Guid.Parse(failed.Id)).InScope.ShouldBeTrue();
    }

    private ExecutableBlueprint Graph()
    {
        delivery.InputChannel.RequiredProperties = ["Address", "Receipt"];
        return new() { Id = 7, InitialNode = lookup, AllNodes = [lookup, payment, delivery] };
    }

    private static ExecutableBlueprintNode Node(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, InputChannel = new() { Name = "Input", Properties = [], OutboundBindings = [] },
        OutputChannels = [], InboundBindings = [], DesignTimeInputOverrides = [], ResolvedVersion = new Version(1, 0)
    };

    private ProcessStepRecord Occurrence(ExecutableBlueprintNode node, ProcessStepStatus status, string? field, string? value,
        params ProcessStepRecord[] parents) => new()
    {
        id = Guid.NewGuid().ToString(), ProcessId = process.Id, BlueprintId = process.BlueprintId,
        BlueprintNodeId = node.Id.ToString(), Status = status,
        OutputData = field is null ? null : new() { ["Success"] = JsonSerializer.SerializeToElement(new Dictionary<string,string?> { [field] = value }) },
        InputHydrationPlan = new()
        {
            InputChannelName = "Input",
            Sources = parents.Select(p =>
            {
                var property = p.BlueprintNodeId == lookup.Id.ToString() ? "Address" : "Receipt";
                return new InputHydrationSource { BindingId = p.Id, SourceProcessStepId = p.Id, SourceOutputChannelName = "Success",
                    PropertyMappings = [new() { SourceProperty = property, TargetProperty = property }] };
            }).ToList()
        }
    };
}

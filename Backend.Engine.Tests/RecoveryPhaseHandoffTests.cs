using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class RecoveryPhaseHandoffTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Recovered_handoff_uses_only_effective_receipts_and_a_replay_preserves_the_next_phase(bool receiptAvailable)
    {
        var childId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var earlier = Guid.NewGuid().ToString();
        var fresh = Guid.NewGuid().ToString();
        var parentId = Guid.NewGuid();
        var metadata = new ProcessPhaseMetadata { PhasedProcessId = parentId.ToString(), PhasedBlueprintDefinitionId = 2,
            PhasedBlueprintVersionId = 3, PhaseId = sourceId.ToString(), PhaseIndex = 0, PhaseName = "Pay" };
        var sourcePhase = new PhasedProcessPhaseRecord { PhaseId = sourceId.ToString(), PhaseIndex = 0, PhaseName = "Pay",
            BlueprintDefinitionId = 10, ChildProcessId = childId.ToString(), Status = PhasedProcessStatus.Failed };
        var parent = new PhasedProcessRecord { id = parentId.ToString(), PhasedBlueprintDefinitionId = "2",
            PhasedBlueprintVersionId = 3, Phases = [sourcePhase], Status = PhasedProcessStatus.Failed };
        var blueprint = new PhasedBlueprintVersion { Id = 3,
            Phases = [new() { Id = sourceId }, new() { Id = targetId, PhaseIndex = 1 }],
            Transitions = [new() { Id = Guid.NewGuid(), SourcePhaseId = sourceId, DestinationPhaseId = targetId,
                HandoffMappings = [new() { SourceBlueprintNodeId = nodeId, SourceChannelName = "out",
                    SourceProperty = "receipt", TargetBlueprintNodeId = Guid.NewGuid(), TargetProperty = "paymentReference" }] }] };
        var child = new ProcessInstanceRecord { id = childId.ToString(), BlueprintId = "8",
            Recovery = new ProcessRecoveryState { EffectiveAttemptIds = new() { [earlier] = fresh } } };
        var designs = new Mock<IPhasedBlueprintRepository>();
        designs.Setup(r => r.GetVersionGraphAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(blueprint);
        var parents = new Mock<IPhasedProcessRepository>();
        parents.Setup(r => r.GetAsync(parentId, 2, It.IsAny<CancellationToken>())).ReturnsAsync(parent);
        parents.Setup(r => r.TryUpdateAsync(parent, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var rows = new Mock<IProcessStepRepository>();
        rows.Setup(r => r.GetProcessStepsForProcess(childId, It.IsAny<CancellationToken>())).ReturnsAsync([
            new ProcessStepRecord { Id = earlier, BlueprintNodeId = nodeId.ToString(), Status = ProcessStepStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow, OutputData = new() { ["out"] = JsonSerializer.SerializeToElement(new { receipt = "superseded" }) } },
            new ProcessStepRecord { Id = fresh, LogicalStepId = earlier, RecoveryId = Guid.NewGuid(), BlueprintNodeId = nodeId.ToString(),
                Status = receiptAvailable ? ProcessStepStatus.Completed : ProcessStepStatus.Skipped,
                OutputData = receiptAvailable ? new() { ["out"] = JsonSerializer.SerializeToElement(new { receipt = "accepted" }) } : null }]);
        var registered = new HashSet<string>();
        var correlations = new Mock<IPhaseCorrelationRepository>();
        correlations.Setup(r => r.CreateAsync(It.IsAny<PhaseCorrelationRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PhaseCorrelationRecord c, CancellationToken _) => { registered.Add(c.Id); return c; });
        var service = new PhasedProcessFinalizationService(designs.Object, parents.Object, correlations.Object,
            Mock.Of<IProcessInstanceRepository>(), rows.Object, NullLogger<PhasedProcessFinalizationService>.Instance);
        Task Complete() => service.FinalizeCompletedPhaseAsync(metadata, 8, childId, DateTimeOffset.UtcNow, CancellationToken.None, child);

        if (!receiptAvailable)
        {
            var exception = await Should.ThrowAsync<MissingRecoveredHandoffException>(Complete);
            exception.SourceNodeId.ShouldBe(nodeId);
            parent.Status.ShouldBe(PhasedProcessStatus.Failed);
            registered.ShouldBeEmpty();
            return;
        }
        await Complete();
        parent.PhaseResults[sourceId.ToString()]["paymentReference"].GetString().ShouldBe("accepted");
        parent.Status.ShouldBe(PhasedProcessStatus.WaitingForTrigger);
        parent.Status = PhasedProcessStatus.InProgress;
        await Complete();
        parent.Status.ShouldBe(PhasedProcessStatus.InProgress);
        registered.Count.ShouldBe(1);
        parents.Verify(r => r.TryUpdateAsync(parent, It.IsAny<CancellationToken>()), Times.Once);
    }
}

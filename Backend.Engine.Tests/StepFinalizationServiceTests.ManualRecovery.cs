using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed partial class StepFinalizationServiceTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_late_checkpoint_only_settles_its_physical_attempt_after_manual_recovery(bool success)
    {
        var processId = Guid.NewGuid();
        var source = Guid.NewGuid();
        var blueprint = CreateSingleInboundBlueprint(source, Guid.NewGuid());
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var finalizations = new InMemoryProcessStepFinalizationRepository();
        var queue = new ProcessStepQueueWriterSpy();
        var finalizer = CreateService(stepRepository, processRepository, finalizations, CreateDispatchService(stepRepository, queue));
        var context = await CreateContextAsync(processId, source, blueprint, stepRepository, processRepository);
        var result = success ? CreateSuccessResult(context, "Out") : CreateFailureResult(context, "old failure");
        var checkpoint = CreateFinalizationRecord(context, result,
            success ? ProcessStepFinalizationOutcome.Succeeded : ProcessStepFinalizationOutcome.Failed,
            ProcessStepFinalizationStage.ResultReceived);
        finalizations.Records[context.ProcessStepId] = checkpoint;
        processRepository.State.Recovery = new ProcessRecoveryState { RecoveryId = Guid.NewGuid(), Revision = 2, Sequence = 2 };
        processRepository.State.Status = ProcessStatus.Completed;

        await finalizer.ResumeStepFinalizationForRecoveryAsync(context, checkpoint, CancellationToken.None);
        var saved = await finalizations.GetRequiredAsync(context.ProcessStepId);
        await finalizer.ResumeStepFinalizationAsync(context, saved, CancellationToken.None);

        saved.Stage.ShouldBe(ProcessStepFinalizationStage.RecoveredWithoutQueueMessage);
        processRepository.State.Status.ShouldBe(ProcessStatus.Completed);
        stepRepository.CreatedSteps.ShouldBeEmpty();
        queue.Messages.ShouldBeEmpty();
        context.QueueMessage.CompleteCount.ShouldBe(1);
    }
}

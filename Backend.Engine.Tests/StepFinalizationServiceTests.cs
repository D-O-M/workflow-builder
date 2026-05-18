using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Engine;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;
using Backend.Repositories;
using Backend.Services.ProcessStepQueue;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class StepFinalizationServiceTests
{
    [Test]
    public async Task Success_finalization_persists_dispatch_intent_when_enqueue_fails_and_worker_later_sends()
    {
        var processId = Guid.NewGuid();
        var sourceNodeId = Guid.NewGuid();
        var destinationNodeId = Guid.NewGuid();
        var blueprint = CreateSingleInboundBlueprint(sourceNodeId, destinationNodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var queueWriter = new ProcessStepQueueWriterSpy { ThrowOnEnqueueAttempts = 1 };
        var dispatchService = CreateDispatchService(stepRepository, queueWriter);
        var processIndex = new ProcessIndexProjectionServiceSpy();
        var service = CreateService(stepRepository, processRepository, finalizationRepository, dispatchService, processIndex);
        var context = await CreateContextAsync(processId, sourceNodeId, blueprint, stepRepository, processRepository);
        var result = CreateSuccessResult(context, "Out");

        await service.FinalizeSucceededStepAsync(context, result, CancellationToken.None);

        var record = await finalizationRepository.GetRequiredAsync(context.ProcessStepId);
        record.Stage.ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
        record.LastErrorKind.ShouldBeNull();
        stepRepository.States[context.ProcessStepId].Status.ShouldBe(ProcessStepStatus.Completed);
        stepRepository.CreatedSteps.Count.ShouldBe(1);
        var downstreamStep = stepRepository.CreatedSteps.Single();
        downstreamStep.Dispatch.ShouldNotBeNull();
        downstreamStep.Dispatch.Status.ShouldBe(ProcessStepDispatchStatus.RetryableDispatchError);
        processIndex.Upserts.Count.ShouldBeGreaterThanOrEqualTo(1);
        processIndex.Upserts.Last().ProcessId.ShouldBe(processId.ToString());
        processIndex.Upserts.Last().Status.ShouldBe(ProcessStatus.InProgress);
        queueWriter.Messages.Count.ShouldBe(0);
        context.QueueMessage.CompleteCount.ShouldBe(1);

        queueWriter.ThrowOnEnqueueAttempts = 0;
        downstreamStep.Dispatch.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await dispatchService.DrainDueDispatchesAsync(10, CancellationToken.None);

        var completedRecord = await finalizationRepository.GetRequiredAsync(context.ProcessStepId);
        completedRecord.Stage.ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
        stepRepository.CreatedSteps.Count.ShouldBe(1);
        queueWriter.Messages.Count.ShouldBe(1);
        queueWriter.Messages[0].BlueprintNodeId.ShouldBe(destinationNodeId);
        downstreamStep.Dispatch.Status.ShouldBe(ProcessStepDispatchStatus.Dispatched);
    }

    [Test]
    public async Task Duplicate_redelivery_after_completed_finalization_does_not_duplicate_downstream_steps()
    {
        var processId = Guid.NewGuid();
        var sourceNodeId = Guid.NewGuid();
        var destinationNodeId = Guid.NewGuid();
        var blueprint = CreateSingleInboundBlueprint(sourceNodeId, destinationNodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var dispatchService = CreateDispatchService(stepRepository, queueWriter);
        var service = CreateService(stepRepository, processRepository, finalizationRepository, dispatchService);
        var context = await CreateContextAsync(processId, sourceNodeId, blueprint, stepRepository, processRepository);

        await service.FinalizeSucceededStepAsync(context, CreateSuccessResult(context, "Out"), CancellationToken.None);

        stepRepository.CreatedSteps.Count.ShouldBe(1);
        queueWriter.Messages.Count.ShouldBe(1);
        var record = await finalizationRepository.GetRequiredAsync(context.ProcessStepId);
        record.Stage.ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);

        var duplicateContext = await CreateContextAsync(
            processId,
            sourceNodeId,
            blueprint,
            stepRepository,
            processRepository,
            claim: false);

        await service.ResumeStepFinalizationAsync(duplicateContext, record, CancellationToken.None);

        stepRepository.CreatedSteps.Count.ShouldBe(1);
        queueWriter.Messages.Count.ShouldBe(1);
        duplicateContext.QueueMessage.CompleteCount.ShouldBe(1);
    }

    [Test]
    public async Task Failure_finalization_can_retry_without_rewriting_failed_step()
    {
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var blueprint = CreateTerminalBlueprint(nodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint))
        {
            ThrowOnTryUpdateAttempts = 1
        };
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(
            stepRepository,
            processRepository,
            finalizationRepository,
            CreateDispatchService(stepRepository, queueWriter));
        var context = await CreateContextAsync(processId, nodeId, blueprint, stepRepository, processRepository);
        var failedAt = DateTimeOffset.UtcNow;
        var result = new PluginExecutionResult(
            context.ProcessStepId,
            Succeeded: false,
            EffectiveInputData: null,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            GlobalState: null,
            StartedAt: failedAt.AddSeconds(-1),
            CompletedAt: failedAt,
            TimingsMs: null,
            ErrorMessage: "boom");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            service.FinalizeFailedStepAsync(context, result, CancellationToken.None));

        stepRepository.FailMutationCount.ShouldBe(1);
        stepRepository.States[context.ProcessStepId].Status.ShouldBe(ProcessStepStatus.Failed);
        var record = await finalizationRepository.GetRequiredAsync(context.ProcessStepId);
        record.Stage.ShouldBe(ProcessStepFinalizationStage.ResultReceived);
        record.LastErrorKind.ShouldBe(ProcessStepFinalizationErrorKind.RetryableFinalizationError);

        processRepository.ThrowOnTryUpdateAttempts = 0;
        var retryContext = await CreateContextAsync(
            processId,
            nodeId,
            blueprint,
            stepRepository,
            processRepository,
            claim: false);

        await service.ResumeStepFinalizationAsync(retryContext, record, CancellationToken.None);

        stepRepository.FailMutationCount.ShouldBe(1);
        retryContext.QueueMessage.CompleteCount.ShouldBe(1);
        (await finalizationRepository.GetRequiredAsync(context.ProcessStepId)).Stage
            .ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
    }

    [Test]
    public async Task Queue_message_is_completed_only_after_terminal_finalization_stage_is_durable()
    {
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var blueprint = CreateTerminalBlueprint(nodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(
            stepRepository,
            processRepository,
            finalizationRepository,
            CreateDispatchService(stepRepository, queueWriter));
        var context = await CreateContextAsync(processId, nodeId, blueprint, stepRepository, processRepository);
        context.QueueMessage.OnComplete = () =>
        {
            var record = finalizationRepository.Records[context.ProcessStepId];
            record.Stage.ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
        };

        await service.FinalizeSucceededStepAsync(
            context,
            CreateSuccessResult(context, outputChannelName: null),
            CancellationToken.None);

        context.QueueMessage.CompleteCount.ShouldBe(1);
    }

    [Test]
    public async Task Collection_iterator_finalization_resumes_fan_out_without_duplicate_steps()
    {
        var processId = Guid.NewGuid();
        var iteratorNodeId = Guid.NewGuid();
        var destinationNodeId = Guid.NewGuid();
        var blueprint = CreateCollectionIteratorBlueprint(iteratorNodeId, destinationNodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var queueWriter = new ProcessStepQueueWriterSpy { ThrowOnEnqueueAttempts = 2 };
        var dispatchService = CreateDispatchService(stepRepository, queueWriter);
        var service = CreateService(stepRepository, processRepository, finalizationRepository, dispatchService);
        var context = await CreateContextAsync(processId, iteratorNodeId, blueprint, stepRepository, processRepository);
        var items = new[] { "alpha", "bravo" };
        var result = CreateCollectionIteratorResult(context, items);

        await service.FinalizeSucceededCollectionIteratorStepAsync(
            context,
            result,
            repeatedBlueprintNodeCount: 1,
            itemCount: items.Length,
            batchSize: 100,
            CancellationToken.None);

        var record = await finalizationRepository.GetRequiredAsync(context.ProcessStepId);
        record.Stage.ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
        stepRepository.CreatedSteps.Count.ShouldBe(2);
        stepRepository.CreatedSteps
            .All(step => step.Dispatch?.Status == ProcessStepDispatchStatus.RetryableDispatchError)
            .ShouldBeTrue();
        queueWriter.Messages.Count.ShouldBe(0);
        context.QueueMessage.CompleteCount.ShouldBe(1);

        queueWriter.ThrowOnEnqueueAttempts = 0;
        foreach (var step in stepRepository.CreatedSteps)
        {
            step.Dispatch!.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        }

        await dispatchService.DrainDueDispatchesAsync(10, CancellationToken.None);

        stepRepository.CreatedSteps.Count.ShouldBe(2);
        queueWriter.Messages.Count.ShouldBe(2);
        queueWriter.Messages.Select(message => message.BlueprintNodeId).ShouldAllBe(id => id == destinationNodeId);
        stepRepository.CreatedSteps
            .All(step => step.Dispatch?.Status == ProcessStepDispatchStatus.Dispatched)
            .ShouldBeTrue();
        (await finalizationRepository.GetRequiredAsync(context.ProcessStepId)).Stage
            .ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
    }

    [Test]
    public async Task Resume_success_finalization_from_each_intermediate_stage_completes_branch()
    {
        var stages = new[]
        {
            ProcessStepFinalizationStage.ResultReceived,
            ProcessStepFinalizationStage.OutputsPersisted,
            ProcessStepFinalizationStage.DownstreamActivationCompleted,
            ProcessStepFinalizationStage.ProcessFinalizationCompleted,
            ProcessStepFinalizationStage.QueueMessageCompletionPrepared
        };

        foreach (var stage in stages)
        {
            var processId = Guid.NewGuid();
            var nodeId = Guid.NewGuid();
            var blueprint = CreateTerminalBlueprint(nodeId);
            var stepRepository = new InMemoryProcessStepRepository();
            var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
            var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
            var service = CreateService(
                stepRepository,
                processRepository,
                finalizationRepository,
                CreateDispatchService(stepRepository, new ProcessStepQueueWriterSpy()));
            var context = await CreateContextAsync(processId, nodeId, blueprint, stepRepository, processRepository);
            var result = CreateSuccessResult(context, outputChannelName: null);
            var record = CreateFinalizationRecord(context, result, ProcessStepFinalizationOutcome.Succeeded, stage);
            finalizationRepository.Records[context.ProcessStepId] = record;
            if (stage is not ProcessStepFinalizationStage.ResultReceived)
            {
                MarkSuccessPersisted(context, stepRepository, processRepository, record);
                TrimResultSnapshot(record);
            }

            var retryContext = await CreateContextAsync(
                processId,
                nodeId,
                blueprint,
                stepRepository,
                processRepository,
                claim: false);

            await service.ResumeStepFinalizationAsync(retryContext, record, CancellationToken.None);

            var completed = await finalizationRepository.GetRequiredAsync(context.ProcessStepId);
            completed.Stage.ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
            retryContext.QueueMessage.CompleteCount.ShouldBe(1);
            stepRepository.CompleteMutationCount.ShouldBe(
                stage is ProcessStepFinalizationStage.ResultReceived ? 1 : 0);
        }
    }

    [Test]
    public async Task Resume_failure_finalization_from_each_intermediate_stage_completes_branch()
    {
        var stages = new[]
        {
            ProcessStepFinalizationStage.ResultReceived,
            ProcessStepFinalizationStage.FailurePersisted,
            ProcessStepFinalizationStage.ProcessFinalizationCompleted,
            ProcessStepFinalizationStage.QueueMessageCompletionPrepared
        };

        foreach (var stage in stages)
        {
            var processId = Guid.NewGuid();
            var nodeId = Guid.NewGuid();
            var blueprint = CreateTerminalBlueprint(nodeId);
            var stepRepository = new InMemoryProcessStepRepository();
            var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
            var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
            var service = CreateService(
                stepRepository,
                processRepository,
                finalizationRepository,
                CreateDispatchService(stepRepository, new ProcessStepQueueWriterSpy()));
            var context = await CreateContextAsync(processId, nodeId, blueprint, stepRepository, processRepository);
            var result = CreateFailureResult(context, "boom");
            var record = CreateFinalizationRecord(context, result, ProcessStepFinalizationOutcome.Failed, stage);
            finalizationRepository.Records[context.ProcessStepId] = record;
            if (stage is not ProcessStepFinalizationStage.ResultReceived)
            {
                MarkFailurePersisted(context, stepRepository, processRepository, record);
                TrimResultSnapshot(record);
            }

            var retryContext = await CreateContextAsync(
                processId,
                nodeId,
                blueprint,
                stepRepository,
                processRepository,
                claim: false);

            await service.ResumeStepFinalizationAsync(retryContext, record, CancellationToken.None);

            var completed = await finalizationRepository.GetRequiredAsync(context.ProcessStepId);
            completed.Stage.ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
            retryContext.QueueMessage.CompleteCount.ShouldBe(1);
            stepRepository.FailMutationCount.ShouldBe(
                stage is ProcessStepFinalizationStage.ResultReceived ? 1 : 0);
        }
    }

    [Test]
    public async Task Queue_message_completion_failure_leaves_durable_terminal_prepared_stage_for_retry()
    {
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var blueprint = CreateTerminalBlueprint(nodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var service = CreateService(
            stepRepository,
            processRepository,
            finalizationRepository,
            CreateDispatchService(stepRepository, new ProcessStepQueueWriterSpy()));
        var context = await CreateContextAsync(processId, nodeId, blueprint, stepRepository, processRepository);
        context.QueueMessage.ThrowOnComplete = true;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            service.FinalizeSucceededStepAsync(
                context,
                CreateSuccessResult(context, outputChannelName: null),
                CancellationToken.None));

        var record = await finalizationRepository.GetRequiredAsync(context.ProcessStepId);
        record.Stage.ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
        record.LastErrorKind.ShouldBe(ProcessStepFinalizationErrorKind.RetryableFinalizationError);
        context.QueueMessage.CompleteCount.ShouldBe(0);
        stepRepository.CompleteMutationCount.ShouldBe(1);

        var retryContext = await CreateContextAsync(
            processId,
            nodeId,
            blueprint,
            stepRepository,
            processRepository,
            claim: false);

        await service.ResumeStepFinalizationAsync(retryContext, record, CancellationToken.None);

        retryContext.QueueMessage.CompleteCount.ShouldBe(1);
        stepRepository.CompleteMutationCount.ShouldBe(1);
        (await finalizationRepository.GetRequiredAsync(context.ProcessStepId)).Stage
            .ShouldBe(ProcessStepFinalizationStage.QueueMessageCompletionPrepared);
    }

    [Test]
    public async Task Recovery_resume_success_from_outputs_persisted_activates_downstream_without_queue_message()
    {
        var processId = Guid.NewGuid();
        var sourceNodeId = Guid.NewGuid();
        var destinationNodeId = Guid.NewGuid();
        var blueprint = CreateSingleInboundBlueprint(sourceNodeId, destinationNodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(
            stepRepository,
            processRepository,
            finalizationRepository,
            CreateDispatchService(stepRepository, queueWriter));
        var context = await CreateContextAsync(processId, sourceNodeId, blueprint, stepRepository, processRepository);
        var result = CreateSuccessResult(context, "Out");
        var record = CreateFinalizationRecord(
            context,
            result,
            ProcessStepFinalizationOutcome.Succeeded,
            ProcessStepFinalizationStage.OutputsPersisted);
        finalizationRepository.Records[context.ProcessStepId] = record;
        MarkSuccessPersisted(context, stepRepository, processRepository, record);
        TrimResultSnapshot(record);

        var recoveryContext = await CreateContextAsync(
            processId,
            sourceNodeId,
            blueprint,
            stepRepository,
            processRepository,
            claim: false);

        await service.ResumeStepFinalizationForRecoveryAsync(
            recoveryContext,
            record,
            CancellationToken.None);

        var recovered = await finalizationRepository.GetRequiredAsync(context.ProcessStepId);
        recovered.Stage.ShouldBe(ProcessStepFinalizationStage.RecoveredWithoutQueueMessage);
        stepRepository.CreatedSteps.Count.ShouldBe(1);
        queueWriter.Messages.Count.ShouldBe(1);
        recoveryContext.QueueMessage.CompleteCount.ShouldBe(0);
    }

    [Test]
    public async Task Recovery_resume_failure_from_failure_persisted_marks_recovered_without_queue_message()
    {
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var blueprint = CreateTerminalBlueprint(nodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var service = CreateService(
            stepRepository,
            processRepository,
            finalizationRepository,
            CreateDispatchService(stepRepository, new ProcessStepQueueWriterSpy()));
        var context = await CreateContextAsync(processId, nodeId, blueprint, stepRepository, processRepository);
        var result = CreateFailureResult(context, "boom");
        var record = CreateFinalizationRecord(
            context,
            result,
            ProcessStepFinalizationOutcome.Failed,
            ProcessStepFinalizationStage.FailurePersisted);
        finalizationRepository.Records[context.ProcessStepId] = record;
        MarkFailurePersisted(context, stepRepository, processRepository, record);
        TrimResultSnapshot(record);

        var recoveryContext = await CreateContextAsync(
            processId,
            nodeId,
            blueprint,
            stepRepository,
            processRepository,
            claim: false);

        await service.ResumeStepFinalizationForRecoveryAsync(
            recoveryContext,
            record,
            CancellationToken.None);

        var recovered = await finalizationRepository.GetRequiredAsync(context.ProcessStepId);
        recovered.Stage.ShouldBe(ProcessStepFinalizationStage.RecoveredWithoutQueueMessage);
        stepRepository.FailMutationCount.ShouldBe(0);
        recoveryContext.QueueMessage.CompleteCount.ShouldBe(0);
    }

    [Test]
    public async Task Later_redelivery_for_recovered_finalization_completes_queue_message_only()
    {
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var blueprint = CreateTerminalBlueprint(nodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var service = CreateService(
            stepRepository,
            processRepository,
            finalizationRepository,
            CreateDispatchService(stepRepository, new ProcessStepQueueWriterSpy()));
        var context = await CreateContextAsync(processId, nodeId, blueprint, stepRepository, processRepository);
        var record = CreateFinalizationRecord(
            context,
            CreateSuccessResult(context, outputChannelName: null),
            ProcessStepFinalizationOutcome.Succeeded,
            ProcessStepFinalizationStage.RecoveredWithoutQueueMessage);
        finalizationRepository.Records[context.ProcessStepId] = record;
        MarkSuccessPersisted(context, stepRepository, processRepository, record);
        TrimResultSnapshot(record);

        var redeliveryContext = await CreateContextAsync(
            processId,
            nodeId,
            blueprint,
            stepRepository,
            processRepository,
            claim: false);

        await service.ResumeStepFinalizationAsync(redeliveryContext, record, CancellationToken.None);

        redeliveryContext.QueueMessage.CompleteCount.ShouldBe(1);
        stepRepository.CompleteMutationCount.ShouldBe(0);
        (await finalizationRepository.GetRequiredAsync(context.ProcessStepId)).Stage
            .ShouldBe(ProcessStepFinalizationStage.RecoveredWithoutQueueMessage);
    }

    [Test]
    public async Task Recovery_service_marks_missing_step_unrecoverable()
    {
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var blueprint = CreateTerminalBlueprint(nodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var service = CreateService(
            stepRepository,
            processRepository,
            finalizationRepository,
            CreateDispatchService(stepRepository, new ProcessStepQueueWriterSpy()));
        var context = await CreateContextAsync(processId, nodeId, blueprint, stepRepository, processRepository);
        var record = CreateFinalizationRecord(
            context,
            CreateSuccessResult(context, outputChannelName: null),
            ProcessStepFinalizationOutcome.Succeeded,
            ProcessStepFinalizationStage.OutputsPersisted);
        record.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        finalizationRepository.Records[context.ProcessStepId] = record;
        stepRepository.States.Remove(context.ProcessStepId);
        var recoveryService = CreateRecoveryService(
            stepRepository,
            processRepository,
            finalizationRepository,
            service,
            blueprint);

        var recovered = await recoveryService.RecoverDueFinalizationsAsync(
            new StepFinalizationRecoveryOptions
            {
                Enabled = true,
                MinRecordAgeSeconds = 1,
                BatchSize = 10,
                MaxRetryCount = 10
            },
            CancellationToken.None);

        recovered.ShouldBe(0);
        record.LastErrorKind.ShouldBe(ProcessStepFinalizationErrorKind.FinalizationFailed);
    }

    [Test]
    public async Task Resume_rejects_cross_path_stage_without_retryable_checkpoint()
    {
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var blueprint = CreateTerminalBlueprint(nodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(processId, blueprint));
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var service = CreateService(
            stepRepository,
            processRepository,
            finalizationRepository,
            CreateDispatchService(stepRepository, new ProcessStepQueueWriterSpy()));
        var context = await CreateContextAsync(processId, nodeId, blueprint, stepRepository, processRepository);
        var record = CreateFinalizationRecord(
            context,
            CreateSuccessResult(context, outputChannelName: null),
            ProcessStepFinalizationOutcome.Succeeded,
            ProcessStepFinalizationStage.FailurePersisted);
        finalizationRepository.Records[context.ProcessStepId] = record;

        await Should.ThrowAsync<ProcessStepFinalizationTransitionException>(() =>
            service.ResumeStepFinalizationAsync(context, record, CancellationToken.None));

        record.LastErrorKind.ShouldBeNull();
        context.QueueMessage.CompleteCount.ShouldBe(0);
    }

    private static StepFinalizationService CreateService(
        InMemoryProcessStepRepository stepRepository,
        InMemoryProcessInstanceRepository processRepository,
        InMemoryProcessStepFinalizationRepository finalizationRepository,
        ProcessStepDispatchService dispatchService,
        IProcessIndexProjectionService? processIndexProjectionService = null)
    {
        var telemetry = new ProcessLifecycleTelemetry(NullLogger<ProcessLifecycleTelemetry>.Instance);
        var stepClaimService = new StepClaimService(
            stepRepository,
            processRepository,
            telemetry,
            processIndexProjectionService,
            NullLogger<StepClaimService>.Instance);
        var activationPersistenceService = new ActivationPersistenceService(
            new InMemoryNodeJoinStateRepository(),
            dispatchService,
            stepRepository,
            NullLogger<ActivationPersistenceService>.Instance);
        var phasedProcessFinalizationService = new PhasedProcessFinalizationService(
            Mock.Of<IPhasedBlueprintRepository>(),
            Mock.Of<IPhasedProcessRepository>(),
            Mock.Of<IPhaseCorrelationRepository>(),
            processRepository,
            stepRepository,
            NullLogger<PhasedProcessFinalizationService>.Instance);
        return new StepFinalizationService(
            new ActivationPlanner(),
            activationPersistenceService,
            phasedProcessFinalizationService,
            finalizationRepository,
            stepRepository,
            processRepository,
            stepClaimService,
            telemetry,
            NullLogger<StepFinalizationService>.Instance,
            processIndexProjectionService);
    }

    private static StepFinalizationRecoveryService CreateRecoveryService(
        InMemoryProcessStepRepository stepRepository,
        InMemoryProcessInstanceRepository processRepository,
        InMemoryProcessStepFinalizationRepository finalizationRepository,
        IStepFinalizationService stepFinalizationService,
        ExecutableBlueprint blueprint) =>
        new(
            finalizationRepository,
            stepRepository,
            processRepository,
            new StaticBlueprintExecutionCache(blueprint),
            stepFinalizationService,
            new StepFinalizationRecoveryTelemetry(),
            NullLogger<StepFinalizationRecoveryService>.Instance,
            NullLogger<StepExecutionContext>.Instance);

    private static ProcessStepDispatchService CreateDispatchService(
        InMemoryProcessStepRepository stepRepository,
        ProcessStepQueueWriterSpy queueWriter) =>
        new(
            stepRepository,
            queueWriter,
            new ProcessStepDispatchOptions
            {
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
                RetryMaxDelay = TimeSpan.FromMilliseconds(10)
            },
            new ProcessStepDispatchTelemetry(),
            NullLogger<ProcessStepDispatchService>.Instance);

    private static async Task<TestStepExecutionContext> CreateContextAsync(
        Guid processId,
        Guid nodeId,
        ExecutableBlueprint blueprint,
        InMemoryProcessStepRepository stepRepository,
        InMemoryProcessInstanceRepository processRepository,
        bool claim = true)
    {
        var processStepId = ProcessStepIdentity.Create(processId, nodeId);
        var node = blueprint.NodesById[nodeId];
        if (!stepRepository.States.ContainsKey(processStepId))
        {
            stepRepository.States[processStepId] = new ProcessStepRecord
            {
                Id = processStepId.ToString(),
                ProcessId = processId.ToString(),
                BlueprintId = blueprint.Id.ToString(),
                BlueprintNodeId = nodeId.ToString(),
                Status = ProcessStepStatus.Pending,
                InputHydrationPlan = new InputHydrationPlan
                {
                    InputChannelName = node.InputChannel.Name
                }
            };
        }

        var message = new ProcessStepInitialisationMessage
        {
            ProcessId = processId,
            ProcessStepId = processStepId,
            ExecutableBlueprintId = blueprint.Id,
            BlueprintNodeId = nodeId
        };
        var queueMessage = new ProcessStepQueueMessageSpy(message);
        var context = new TestStepExecutionContext(
            message,
            queueMessage,
            blueprint);

        if (claim)
        {
            var claimResult = await new StepClaimService(
                    stepRepository,
                    processRepository,
                    new ProcessLifecycleTelemetry(NullLogger<ProcessLifecycleTelemetry>.Instance))
                .TryClaimStepAsync(context, CancellationToken.None);
            claimResult.StepClaimed.ShouldBeTrue();
        }

        return context;
    }

    private static PluginExecutionResult CreateSuccessResult(
        StepExecutionContext context,
        string? outputChannelName) =>
        new(
            context.ProcessStepId,
            Succeeded: true,
            EffectiveInputData: null,
            OutputData: outputChannelName is null
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [outputChannelName] = Json(new
                    {
                        Message = "hello"
                    })
                },
            GlobalState: null,
            StartedAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAt: DateTimeOffset.UtcNow,
            TimingsMs: null,
            ErrorMessage: null);

    private static PluginExecutionResult CreateFailureResult(
        StepExecutionContext context,
        string errorMessage)
    {
        var failedAt = DateTimeOffset.UtcNow;
        return new PluginExecutionResult(
            context.ProcessStepId,
            Succeeded: false,
            EffectiveInputData: null,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            GlobalState: null,
            StartedAt: failedAt.AddSeconds(-1),
            CompletedAt: failedAt,
            TimingsMs: null,
            ErrorMessage: errorMessage);
    }

    private static ProcessStepFinalizationRecord CreateFinalizationRecord(
        StepExecutionContext context,
        PluginExecutionResult result,
        ProcessStepFinalizationOutcome outcome,
        ProcessStepFinalizationStage stage) =>
        new()
        {
            id = context.ProcessStepId.ToString(),
            ProcessId = context.ProcessId.ToString(),
            BlueprintId = context.ExecutableBlueprintId.ToString(),
            BlueprintNodeId = context.BlueprintNodeId.ToString(),
            TaskDefinitionId = context.TaskDefinitionId,
            ResolvedTaskVersionId = context.ResolvedTaskVersionId,
            ResolvedTaskVersion = context.BlueprintNode.ResolvedVersion.ToString(),
            Outcome = outcome,
            Source = ProcessStepFinalizationSource.Plugin,
            Stage = stage,
            EffectiveInputData = result.EffectiveInputData,
            OutputData = result.OutputData,
            GlobalState = result.GlobalState,
            TimingsMs = result.TimingsMs is null
                ? null
                : new Dictionary<string, double>(result.TimingsMs, StringComparer.Ordinal),
            ErrorMessage = result.ErrorMessage,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static void MarkSuccessPersisted(
        StepExecutionContext context,
        InMemoryProcessStepRepository stepRepository,
        InMemoryProcessInstanceRepository processRepository,
        ProcessStepFinalizationRecord record)
    {
        var step = stepRepository.States[context.ProcessStepId];
        step.Status = ProcessStepStatus.Completed;
        step.OutputData = record.OutputData is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(record.OutputData, StringComparer.Ordinal);
        step.CompletedAt = record.CompletedAt;
        step.UpdatedAt = record.CompletedAt;

        var process = processRepository.State;
        EnsureExecutionProgress(context, process);
        process.ExecutionProgress.CompletedNodeIds.Add(context.BlueprintNodeId.ToString());
        process.ExecutionProgress.CompletedStepIds.Add(context.ProcessStepId.ToString());
        process.Status = ProcessStatus.Completed;
        process.UpdatedAt = record.CompletedAt;
    }

    private static void MarkFailurePersisted(
        StepExecutionContext context,
        InMemoryProcessStepRepository stepRepository,
        InMemoryProcessInstanceRepository processRepository,
        ProcessStepFinalizationRecord record)
    {
        var step = stepRepository.States[context.ProcessStepId];
        step.Status = ProcessStepStatus.Failed;
        step.ErrorMessage = record.ErrorMessage;
        step.FailedAt = record.CompletedAt;
        step.UpdatedAt = record.CompletedAt;

        var process = processRepository.State;
        EnsureExecutionProgress(context, process);
        process.ExecutionProgress.FailedNodeIds.Add(context.BlueprintNodeId.ToString());
        process.ExecutionProgress.FailedStepIds.Add(context.ProcessStepId.ToString());
        process.Status = ProcessStatus.Failed;
        process.UpdatedAt = record.CompletedAt;
    }

    private static void TrimResultSnapshot(ProcessStepFinalizationRecord record)
    {
        record.EffectiveInputData = null;
        record.OutputData = null;
        record.GlobalState = null;
        record.TimingsMs = null;
    }

    private static void EnsureExecutionProgress(
        StepExecutionContext context,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> process)
    {
        process.ExecutionProgress ??= new ProcessExecutionProgress();
        if (process.ExecutionProgress.TotalNodeCount <= 0)
        {
            process.ExecutionProgress.TotalNodeCount = context.ExecutableBlueprint.AllNodes.Count;
        }

        if (process.ExecutionProgress.TotalStepCount <= 0)
        {
            process.ExecutionProgress.TotalStepCount = process.ExecutionProgress.TotalNodeCount;
        }
    }

    private static PluginExecutionResult CreateCollectionIteratorResult(
        StepExecutionContext context,
        IReadOnlyCollection<string> items)
    {
        var materializedInput = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["items"] = Json(items)
        };

        return new PluginExecutionResult(
            context.ProcessStepId,
            Succeeded: true,
            EffectiveInputData: BlueprintInputMaterializer.BuildChannelPayload(
                context.InputChannelName,
                materializedInput),
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            GlobalState: null,
            StartedAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAt: DateTimeOffset.UtcNow,
            TimingsMs: null,
            ErrorMessage: null);
    }

    private static ExecutableBlueprint CreateSingleInboundBlueprint(
        Guid sourceNodeId,
        Guid destinationNodeId)
    {
        var binding = new ExecutableBlueprintBinding
        {
            Id = "source-to-destination",
            SourceBlueprintNodeId = sourceNodeId,
            SourceChannelName = "Out",
            DestinationBlueprintNodeId = destinationNodeId,
            PropertyMappings =
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "Message",
                    TargetProperty = "Text"
                }
            ]
        };
        var source = CreateNode(sourceNodeId, "SourceInput", "Out", [binding]);
        var destination = CreateNode(destinationNodeId, "DestinationInput", null, [], [binding]);
        return CreateBlueprint(source, [source, destination]);
    }

    private static ExecutableBlueprint CreateTerminalBlueprint(Guid nodeId)
    {
        var node = CreateNode(nodeId, "Input", null, []);
        return CreateBlueprint(node, [node]);
    }

    private static ExecutableBlueprint CreateCollectionIteratorBlueprint(
        Guid iteratorNodeId,
        Guid destinationNodeId)
    {
        var binding = new ExecutableBlueprintBinding
        {
            Id = "iterator-to-destination",
            SourceBlueprintNodeId = iteratorNodeId,
            SourceChannelName = "Item",
            DestinationBlueprintNodeId = destinationNodeId,
            PropertyMappings =
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "item",
                    TargetProperty = "Text"
                }
            ]
        };
        var iterator = new ExecutableBlueprintNode
        {
            Id = iteratorNodeId,
            Name = "Collection Iterator",
            NodeKind = ExecutableBlueprintNodeKind.CollectionIterator,
            TaskDefinitionId = 0,
            CollectionIteratorItemKind = "String",
            AllowedMajor = 0,
            AllowedMinor = 0,
            ResolvedVersion = new Version(0, 0, 0),
            ResolvedTaskVersionId = 0,
            InputChannel = new ExecutableBlueprintChannel
            {
                Id = 1,
                Name = "Collection",
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["items"] = "String[]"
                },
                OutboundBindings = []
            },
            OutputChannels = new Dictionary<string, ExecutableBlueprintChannel>(StringComparer.Ordinal)
            {
                ["Item"] = new()
                {
                    Id = 1,
                    Name = "Item",
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["item"] = "String"
                    },
                    OutboundBindings = [binding]
                }
            },
            InboundBindings = [],
            DesignTimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        };
        var destination = CreateNode(destinationNodeId, "DestinationInput", null, [], [binding]);
        return CreateBlueprint(iterator, [iterator, destination]);
    }

    private static ExecutableBlueprintNode CreateNode(
        Guid nodeId,
        string inputChannelName,
        string? outputChannelName,
        List<ExecutableBlueprintBinding> outboundBindings,
        List<ExecutableBlueprintBinding>? inboundBindings = null)
    {
        var outputChannels = new Dictionary<string, ExecutableBlueprintChannel>(StringComparer.Ordinal);
        if (outputChannelName is not null)
        {
            outputChannels[outputChannelName] = new ExecutableBlueprintChannel
            {
                Id = 1,
                Name = outputChannelName,
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Message"] = "String"
                },
                OutboundBindings = outboundBindings
            };
        }

        return new ExecutableBlueprintNode
        {
            Id = nodeId,
            Name = inputChannelName,
            TaskDefinitionId = 10,
            AllowedMajor = 1,
            AllowedMinor = 0,
            ResolvedVersion = new Version(1, 0, 0),
            ResolvedTaskVersionId = 100,
            ExecutionOrder = 0,
            InputChannel = new ExecutableBlueprintChannel
            {
                Id = 1,
                Name = inputChannelName,
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Text"] = "String"
                },
                OutboundBindings = []
            },
            OutputChannels = outputChannels,
            InboundBindings = inboundBindings ?? [],
            DesignTimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        };
    }

    private static ExecutableBlueprint CreateBlueprint(
        ExecutableBlueprintNode initialNode,
        List<ExecutableBlueprintNode> nodes) =>
        new()
        {
            Id = 101,
            DefinitionId = 10,
            VersionNumber = 1,
            Name = "Finalization Blueprint",
            InitialNode = initialNode,
            AllNodes = nodes
        };

    private static ProcessInstanceRecord<Dictionary<string, JsonElement>> CreateProcessState(
        Guid processId,
        ExecutableBlueprint blueprint) =>
        new()
        {
            id = processId.ToString(),
            BlueprintId = blueprint.Id.ToString(),
            Status = ProcessStatus.InProgress,
            GlobalState = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            NodeOverrides = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal),
            ExecutionProgress = new ProcessExecutionProgress
            {
                TotalNodeCount = blueprint.AllNodes.Count,
                TotalStepCount = blueprint.AllNodes.Count
            }
        };

    private static JsonElement Json<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private sealed class TestStepExecutionContext : StepExecutionContext
    {
        public TestStepExecutionContext(
            ProcessStepInitialisationMessage message,
            ProcessStepQueueMessageSpy queueMessage,
            ExecutableBlueprint executableBlueprint)
            : base(
                message,
                NullLogger<StepExecutionContext>.Instance,
                queueMessage,
                executableBlueprint)
        {
            QueueMessage = queueMessage;
        }

        public ProcessStepQueueMessageSpy QueueMessage { get; }
    }

    private sealed class InMemoryProcessStepRepository : IProcessStepRepository
    {
        public Dictionary<Guid, ProcessStepRecord> States { get; } = new();
        public List<ProcessStepRecord> CreatedSteps { get; } = [];
        public int CompleteMutationCount { get; private set; }
        public int FailMutationCount { get; private set; }

        public Task<ProcessStepRecord> CreateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken)
        {
            States[Guid.Parse(state.Id)] = state;
            CreatedSteps.Add(state);
            return Task.FromResult(state);
        }

        public Task<(bool Created, ProcessStepRecord State)> CreateProcessStepIfNotExists(
            ProcessStepRecord state,
            CancellationToken cancellationToken)
        {
            var processStepId = Guid.Parse(state.Id);
            if (States.TryGetValue(processStepId, out var existing))
            {
                return Task.FromResult((false, existing));
            }

            States[processStepId] = state;
            CreatedSteps.Add(state);
            return Task.FromResult((true, state));
        }

        public async Task<IReadOnlyList<(bool Created, ProcessStepRecord State)>> CreateProcessStepsIfNotExists(
            IReadOnlyCollection<ProcessStepRecord> states,
            CancellationToken cancellationToken)
        {
            var results = new List<(bool Created, ProcessStepRecord State)>(states.Count);
            foreach (var state in states)
            {
                results.Add(await CreateProcessStepIfNotExists(state, cancellationToken));
            }

            return results;
        }

        public Task<ProcessStepRecord> GetProcessStepById(
            Guid taskInstanceId,
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(States[taskInstanceId]);

        public Task<List<ProcessStepRecord>> GetProcessStepsForProcess(
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(States.Values
                .Where(state => string.Equals(state.ProcessId, processId.ToString(), StringComparison.Ordinal))
                .ToList());

        public Task<IReadOnlyList<ProcessStepRecord>> GetDueStepDispatchesAsync(
            DateTimeOffset now,
            int maxItemCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProcessStepRecord>>(States.Values
                .Where(state => state.Status is ProcessStepStatus.Pending && IsDispatchDue(state.Dispatch, now))
                .OrderBy(state => state.CreatedAt)
                .Take(maxItemCount)
                .ToList());

        public Task<ProcessStepDispatchBacklogSnapshot> GetDispatchBacklogSnapshotAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var pending = States.Values
                .Where(state => state.Status is ProcessStepStatus.Pending &&
                    (state.Dispatch is null ||
                     state.Dispatch.Status is ProcessStepDispatchStatus.Pending or ProcessStepDispatchStatus.Dispatching or ProcessStepDispatchStatus.RetryableDispatchError))
                .OrderBy(state => state.CreatedAt)
                .ToList();
            var oldest = pending.FirstOrDefault();
            return Task.FromResult(new ProcessStepDispatchBacklogSnapshot(
                pending.Count,
                oldest?.Dispatch?.CreatedAt ?? oldest?.CreatedAt));
        }

        public Task<(bool Claimed, ProcessStepRecord? State)> TryClaimStepDispatchAsync(
            ProcessStepRecord state,
            string leaseId,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            var processStepId = Guid.Parse(state.Id);
            if (!States.TryGetValue(processStepId, out var current) ||
                current.Status is not ProcessStepStatus.Pending ||
                !IsDispatchDue(current.Dispatch, now))
            {
                return Task.FromResult<(bool, ProcessStepRecord?)>((false, null));
            }

            current.Dispatch ??= ProcessStepDispatchService.CreatePendingDispatchState(current.CreatedAt);
            current.Dispatch.Status = ProcessStepDispatchStatus.Dispatching;
            current.Dispatch.MessageId = current.Id;
            current.Dispatch.AttemptCount++;
            current.Dispatch.UpdatedAt = now;
            current.Dispatch.NextAttemptAt = null;
            current.Dispatch.DispatchingStartedAt = now;
            current.Dispatch.LockedUntil = now.Add(leaseDuration);
            current.Dispatch.LeaseId = leaseId;
            return Task.FromResult<(bool, ProcessStepRecord?)>((true, current));
        }

        public Task<bool> MarkStepDispatchSucceededAsync(
            ProcessStepRecord state,
            string leaseId,
            DateTimeOffset dispatchedAt,
            CancellationToken cancellationToken)
        {
            if (!TryGetLeasedDispatch(state, leaseId, out var dispatch))
            {
                return Task.FromResult(false);
            }

            dispatch.Status = ProcessStepDispatchStatus.Dispatched;
            dispatch.MessageId = state.Id;
            dispatch.UpdatedAt = dispatchedAt;
            dispatch.DispatchedAt = dispatchedAt;
            dispatch.NextAttemptAt = null;
            dispatch.DispatchingStartedAt = null;
            dispatch.LockedUntil = null;
            dispatch.LeaseId = null;
            dispatch.LastErrorKind = null;
            dispatch.LastErrorMessage = null;
            dispatch.LastErrorAt = null;
            return Task.FromResult(true);
        }

        public Task<bool> MarkStepDispatchRetryableErrorAsync(
            ProcessStepRecord state,
            string leaseId,
            ProcessStepDispatchErrorKind errorKind,
            string errorMessage,
            DateTimeOffset failedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken)
        {
            if (!TryGetLeasedDispatch(state, leaseId, out var dispatch))
            {
                return Task.FromResult(false);
            }

            dispatch.Status = ProcessStepDispatchStatus.RetryableDispatchError;
            dispatch.UpdatedAt = failedAt;
            dispatch.NextAttemptAt = nextAttemptAt;
            dispatch.DispatchingStartedAt = null;
            dispatch.LockedUntil = null;
            dispatch.LeaseId = null;
            dispatch.LastErrorKind = errorKind;
            dispatch.LastErrorMessage = errorMessage;
            dispatch.LastErrorAt = failedAt;
            return Task.FromResult(true);
        }

        public Task<bool> MarkStepDispatchSkippedTerminalAsync(
            ProcessStepRecord state,
            string leaseId,
            DateTimeOffset skippedAt,
            CancellationToken cancellationToken)
        {
            if (!TryGetLeasedDispatch(state, leaseId, out var dispatch))
            {
                return Task.FromResult(false);
            }

            dispatch.Status = ProcessStepDispatchStatus.DispatchSkippedTerminal;
            dispatch.UpdatedAt = skippedAt;
            dispatch.NextAttemptAt = null;
            dispatch.DispatchingStartedAt = null;
            dispatch.LockedUntil = null;
            dispatch.LeaseId = null;
            return Task.FromResult(true);
        }

        public Task<ProcessStepRecord> UpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken)
        {
            States[Guid.Parse(state.Id)] = state;
            return Task.FromResult(state);
        }

        public Task<bool> TryUpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken)
        {
            States[Guid.Parse(state.Id)] = state;
            return Task.FromResult(true);
        }

        public Task<(bool Claimed, ProcessStepRecord? State)> TryClaimProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            int taskDefinitionId,
            int allowedMajor,
            int allowedMinor,
            int resolvedTaskVersionId,
            string resolvedTaskVersion,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken)
        {
            if (!States.TryGetValue(taskInstanceId, out var state) ||
                state.Status is not ProcessStepStatus.Pending)
            {
                return Task.FromResult<(bool, ProcessStepRecord?)>((false, null));
            }

            state.Status = ProcessStepStatus.InProgress;
            state.TaskDefinitionId = taskDefinitionId;
            state.AllowedMajor = allowedMajor;
            state.AllowedMinor = allowedMinor;
            state.ResolvedTaskVersionId = resolvedTaskVersionId;
            state.ResolvedTaskVersion = resolvedTaskVersion;
            state.StartedAt = startedAt;
            state.UpdatedAt = startedAt;
            return Task.FromResult<(bool, ProcessStepRecord?)>((true, state));
        }

        public Task CompleteProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            Dictionary<string, JsonElement>? effectiveInputData,
            Dictionary<string, JsonElement> outputData,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            var state = States[taskInstanceId];
            if (state.Status is ProcessStepStatus.Completed or ProcessStepStatus.Failed)
            {
                return Task.CompletedTask;
            }

            CompleteMutationCount++;
            state.Status = ProcessStepStatus.Completed;
            state.InputData = effectiveInputData;
            state.OutputData = outputData;
            state.CompletedAt = completedAt;
            state.FailedAt = null;
            state.ErrorMessage = null;
            state.UpdatedAt = completedAt;
            return Task.CompletedTask;
        }

        public Task FailProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            Dictionary<string, JsonElement>? effectiveInputData,
            string? errorMessage,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken)
        {
            var state = States[taskInstanceId];
            if (state.Status is ProcessStepStatus.Completed or ProcessStepStatus.Failed)
            {
                return Task.CompletedTask;
            }

            FailMutationCount++;
            state.Status = ProcessStepStatus.Failed;
            state.InputData = effectiveInputData;
            state.OutputData = null;
            state.ErrorMessage = errorMessage;
            state.FailedAt = failedAt;
            state.UpdatedAt = failedAt;
            return Task.CompletedTask;
        }

        private static bool IsDispatchDue(
            ProcessStepDispatchState? dispatch,
            DateTimeOffset now) =>
            dispatch is null ||
            dispatch.Status is ProcessStepDispatchStatus.Pending ||
            dispatch.Status is ProcessStepDispatchStatus.RetryableDispatchError &&
                (!dispatch.NextAttemptAt.HasValue || dispatch.NextAttemptAt.Value <= now) ||
            dispatch.Status is ProcessStepDispatchStatus.Dispatching &&
                dispatch.LockedUntil.HasValue &&
                dispatch.LockedUntil.Value <= now;

        private bool TryGetLeasedDispatch(
            ProcessStepRecord state,
            string leaseId,
            out ProcessStepDispatchState dispatch)
        {
            dispatch = null!;
            if (!States.TryGetValue(Guid.Parse(state.Id), out var current) ||
                current.Dispatch is null ||
                !string.Equals(current.Dispatch.LeaseId, leaseId, StringComparison.Ordinal))
            {
                return false;
            }

            dispatch = current.Dispatch;
            return true;
        }
    }

    private sealed class InMemoryProcessInstanceRepository
        : IProcessInstanceRepository<Dictionary<string, JsonElement>>
    {
        public InMemoryProcessInstanceRepository(ProcessInstanceRecord<Dictionary<string, JsonElement>> state)
        {
            State = state;
        }

        public ProcessInstanceRecord<Dictionary<string, JsonElement>> State { get; private set; }
        public int ThrowOnTryUpdateAttempts { get; set; }

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> CreateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> GetProcessStateById(
            Guid processId,
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(State);

        public Task<List<ProcessInstanceRecord<Dictionary<string, JsonElement>>>> GetProcessStatesForBlueprint(
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStateSummary?> GetLatestProcessStateForBlueprint(
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
            int blueprintVersionId,
            string? continuationToken,
            int maxItemCount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
            int blueprintVersionId,
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> UpdateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken)
        {
            State = state;
            return Task.FromResult(state);
        }

        public Task<bool> TryUpdateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken)
        {
            State = state;
            if (ThrowOnTryUpdateAttempts > 0)
            {
                ThrowOnTryUpdateAttempts--;
                throw new InvalidOperationException("Process state update failed.");
            }

            return Task.FromResult(true);
        }
    }

    private sealed class StaticBlueprintExecutionCache : IBlueprintExecutionCache
    {
        private readonly ExecutableBlueprint _blueprint;

        public StaticBlueprintExecutionCache(ExecutableBlueprint blueprint)
        {
            _blueprint = blueprint;
        }

        public Task<ExecutableBlueprint> GetExecutableBlueprintAsync(
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_blueprint);
    }

    private sealed class InMemoryProcessStepFinalizationRepository : IProcessStepFinalizationRepository
    {
        public Dictionary<Guid, ProcessStepFinalizationRecord> Records { get; } = new();

        public Task<ProcessStepFinalizationRecord?> GetAsync(
            Guid processStepId,
            CancellationToken cancellationToken)
        {
            Records.TryGetValue(processStepId, out var record);
            return Task.FromResult(record);
        }

        public Task<ProcessStepFinalizationRecord> GetRequiredAsync(Guid processStepId) =>
            Task.FromResult(Records[processStepId]);

        public Task<(bool Created, ProcessStepFinalizationRecord Record)> CreateIfNotExistsAsync(
            ProcessStepFinalizationRecord record,
            CancellationToken cancellationToken)
        {
            var processStepId = Guid.Parse(record.Id);
            if (Records.TryGetValue(processStepId, out var existing))
            {
                return Task.FromResult((false, existing));
            }

            Records[processStepId] = record;
            return Task.FromResult((true, record));
        }

        public Task<bool> TryUpdateAsync(
            ProcessStepFinalizationRecord record,
            CancellationToken cancellationToken)
        {
            Records[Guid.Parse(record.Id)] = record;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ProcessStepFinalizationRecord>> GetRecoverableFinalizationsAsync(
            DateTimeOffset now,
            TimeSpan minRecordAge,
            int maxRetryCount,
            int maxItemCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProcessStepFinalizationRecord>>(Records.Values
                .Where(record => IsRecoverable(record, now, minRecordAge, maxRetryCount))
                .OrderBy(record => record.UpdatedAt ?? record.CreatedAt)
                .Take(maxItemCount)
                .ToList());

        public Task<ProcessStepFinalizationRecoveryBacklogSnapshot> GetRecoveryBacklogSnapshotAsync(
            DateTimeOffset now,
            TimeSpan minRecordAge,
            int maxRetryCount,
            CancellationToken cancellationToken)
        {
            var records = Records.Values
                .Where(record => IsRecoverable(record, now, minRecordAge, maxRetryCount))
                .OrderBy(record => record.UpdatedAt ?? record.CreatedAt)
                .ToList();
            return Task.FromResult(new ProcessStepFinalizationRecoveryBacklogSnapshot(
                records.Count,
                records.FirstOrDefault()?.UpdatedAt ?? records.FirstOrDefault()?.CreatedAt));
        }

        private static bool IsRecoverable(
            ProcessStepFinalizationRecord record,
            DateTimeOffset now,
            TimeSpan minRecordAge,
            int maxRetryCount)
        {
            var updatedAt = record.UpdatedAt ?? record.CreatedAt;
            if (updatedAt > now - minRecordAge ||
                record.LastErrorKind is ProcessStepFinalizationErrorKind.FinalizationFailed ||
                record.RetryCount >= maxRetryCount ||
                ProcessStepFinalizationStageMachine.IsRecoveredWithoutQueueMessage(record.Stage))
            {
                return false;
            }

            return !ProcessStepFinalizationStageMachine.IsQueueMessageCompletionPrepared(record.Stage) ||
                record.LastErrorKind is not null;
        }
    }

    private sealed class InMemoryNodeJoinStateRepository : INodeJoinStateRepository
    {
        public Task<NodeJoinStateRecord?> GetJoinStateAsync(
            Guid processId,
            Guid blueprintNodeId,
            CancellationToken cancellationToken) =>
            Task.FromResult<NodeJoinStateRecord?>(null);

        public Task<(bool Created, NodeJoinStateRecord State)> CreateJoinStateIfNotExistsAsync(
            NodeJoinStateRecord state,
            CancellationToken cancellationToken) =>
            Task.FromResult((true, state));

        public Task<bool> TryUpdateJoinStateAsync(
            NodeJoinStateRecord state,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class ProcessStepQueueWriterSpy : IProcessStepQueueWriter
    {
        public List<ProcessStepInitialisationMessage> Messages { get; } = [];
        public int ThrowOnEnqueueAttempts { get; set; }

        public Task EnqueueAsync(
            ProcessStepInitialisationMessage message,
            CancellationToken cancellationToken)
        {
            if (ThrowOnEnqueueAttempts > 0)
            {
                ThrowOnEnqueueAttempts--;
                throw new InvalidOperationException("Queue enqueue failed.");
            }

            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ProcessIndexProjectionServiceSpy : IProcessIndexProjectionService
    {
        public List<ProcessInstanceRecord<Dictionary<string, JsonElement>>> Upserts { get; } = [];

        public Task UpsertFromProcessAsync(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> process,
            int blueprintDefinitionId,
            int blueprintVersionId,
            CancellationToken cancellationToken)
        {
            Upserts.Add(process);
            return Task.CompletedTask;
        }

        public Task<ProcessDirectoryRecord?> GetDirectoryByProcessIdAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessSummaryProjectionPage> GetRecentTenantSummariesAsync(
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessSummaryProjectionPage> GetRecentBlueprintSummariesAsync(
            int blueprintDefinitionId,
            IReadOnlyCollection<int>? blueprintVersionIds,
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ProcessStepQueueMessageSpy : IProcessStepQueueMessage
    {
        public ProcessStepQueueMessageSpy(ProcessStepInitialisationMessage payload)
        {
            Payload = payload;
        }

        public string MessageId { get; } = Guid.NewGuid().ToString();
        public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LockedUntil { get; private set; } = DateTimeOffset.UtcNow.AddMinutes(1);
        public ProcessStepInitialisationMessage Payload { get; }
        public int CompleteCount { get; private set; }
        public int AbandonCount { get; private set; }
        public Action? OnComplete { get; set; }
        public bool ThrowOnComplete { get; set; }

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            OnComplete?.Invoke();
            if (ThrowOnComplete)
            {
                throw new InvalidOperationException("Queue message completion failed.");
            }

            CompleteCount++;
            return Task.CompletedTask;
        }

        public Task AbandonAsync(CancellationToken cancellationToken)
        {
            AbandonCount++;
            return Task.CompletedTask;
        }

        public Task RenewLockAsync(CancellationToken cancellationToken)
        {
            LockedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            return Task.CompletedTask;
        }
    }
}

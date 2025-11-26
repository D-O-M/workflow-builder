namespace Backend.Plugins.Communication.Protocols.JobCompletion;

/// <summary>
/// Executes the job completion protocol from the plugin's perspective as the initiator.
/// Walks the shared protocol definition in order and invokes the initiator operations.
/// </summary>
public class JobCompletionInitiatorSteps
{
    private readonly IJobCompletionInitiator _initiator;
    private readonly IReadOnlyList<IProtocolStep<JobCompletionSignal>> _steps;

    public JobCompletionInitiatorSteps(IJobCompletionInitiator initiator)
    {
        _initiator = initiator ?? throw new ArgumentNullException(nameof(initiator));
        _steps = JobCompletionProtocol.Definition.Steps;
    }

    public async Task Perform()
    {
        foreach (var step in _steps)
        {
            switch (step)
            {
                case ProtocolStep<JobCompletionSignal, IJobCompletionInitiator, IJobCompletionAcceptor> initiatorStep:
                    await ExecuteInitiatorStep(initiatorStep);
                    break;

                case ProtocolStep<JobCompletionSignal, IJobCompletionAcceptor, IJobCompletionInitiator> acceptorStep:
                    await AwaitAcceptorStep(acceptorStep);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown protocol step type: {step.GetType().Name}");
            }
        }
    }

    private async Task ExecuteInitiatorStep(
        ProtocolStep<JobCompletionSignal, IJobCompletionInitiator, IJobCompletionAcceptor> step)
    {
        if (step.SendOperation is not null)
        {
            var op = step.SendOperation.Compile();
            await op(_initiator);
            return;
        }

        if (step.CommunicationMethod != CommunicationMethod.RingBuffer)
        {
            throw new InvalidOperationException(
                $"Job completion protocol must use ring buffer, got: {step.CommunicationMethod}");
        }

        await _initiator.SendSignalViaRingBuffer(step.Signal);
    }

    private async Task AwaitAcceptorStep(
        ProtocolStep<JobCompletionSignal, IJobCompletionAcceptor, IJobCompletionInitiator> step)
    {
        if (step.ReceiveOperation is not null)
        {
            var op = step.ReceiveOperation.Compile();
            await op.Invoke(_initiator);
            return;
        }

        if (step.CommunicationMethod != CommunicationMethod.RingBuffer)
        {
            throw new InvalidOperationException(
                $"Job completion protocol must use ring buffer, got: {step.CommunicationMethod}");
        }

        await _initiator.AwaitResponseFromAcceptorViaRingBuffer(step.Signal);
    }
}

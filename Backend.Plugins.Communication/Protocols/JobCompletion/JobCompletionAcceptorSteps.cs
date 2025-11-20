namespace Backend.Plugins.Communication.Protocols.JobCompletion;

/// <summary>
/// Executes the job completion protocol from the engine's perspective as the acceptor.
/// Walks the shared protocol definition in order and invokes the acceptor operations.
/// </summary>
public class JobCompletionAcceptorSteps
{
    private readonly IJobCompletionAcceptor _acceptor;
    private readonly IReadOnlyList<IProtocolStep<JobCompletionSignal>> _steps;

    public JobCompletionAcceptorSteps(IJobCompletionAcceptor acceptor)
    {
        _acceptor = acceptor ?? throw new ArgumentNullException(nameof(acceptor));
        _steps = JobCompletionProtocol.Definition.Steps;
    }

    public async Task Perform()
    {
        foreach (var step in _steps)
        {
            switch (step)
            {
                case ProtocolStep<JobCompletionSignal, IJobCompletionAcceptor, IJobCompletionInitiator> acceptorStep:
                    await ExecuteAcceptorStep(acceptorStep);
                    break;

                case ProtocolStep<JobCompletionSignal, IJobCompletionInitiator, IJobCompletionAcceptor> initiatorStep:
                    await AwaitInitiatorStep(initiatorStep);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown protocol step type: {step.GetType().Name}");
            }
        }
    }

    private async Task ExecuteAcceptorStep(
        ProtocolStep<JobCompletionSignal, IJobCompletionAcceptor, IJobCompletionInitiator> step)
    {
        if (step.SendOperation is not null)
        {
            var op = step.SendOperation.Compile();
            await op(_acceptor);
            return;
        }

        if (step.CommunicationMethod != CommunicationMethod.RingBuffer)
        {
            throw new InvalidOperationException(
                $"Job completion protocol must use ring buffer, got: {step.CommunicationMethod}");
        }

        await _acceptor.SendSignalViaRingBuffer(step.Signal);
    }

    private async Task AwaitInitiatorStep(
        ProtocolStep<JobCompletionSignal, IJobCompletionInitiator, IJobCompletionAcceptor> step)
    {
        if (step.ReceiveOperation is not null)
        {
            var op = step.ReceiveOperation.Compile();
            await op.Invoke(_acceptor);
            return;
        }

        if (step.CommunicationMethod != CommunicationMethod.RingBuffer)
        {
            throw new InvalidOperationException(
                $"Job completion protocol must use ring buffer, got: {step.CommunicationMethod}");
        }

        await _acceptor.AwaitMessageFromInitiatorViaRingBuffer(step.Signal);
    }
}

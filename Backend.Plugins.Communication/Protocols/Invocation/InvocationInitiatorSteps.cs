namespace Backend.Plugins.Communication.Protocols.Invocation;

/// <summary>
/// Executes the invocation protocol from the engine's perspective as the initiator.
/// Walks the shared protocol definition in order and invokes the initiator operations.
/// </summary>
public class InvocationInitiatorSteps
{
    private readonly IInvocationInitiator _initiator;
    private readonly IReadOnlyList<IProtocolStep<InvocationSignal>> _steps;

    public InvocationInitiatorSteps(IInvocationInitiator initiator)
    {
        _initiator = initiator ?? throw new ArgumentNullException(nameof(initiator));
        _steps = InvocationProtocol.Definition.Steps;
    }

    public async Task Perform()
    {
        foreach (var step in _steps)
        {
            switch (step)
            {
                case ProtocolStep<InvocationSignal, IInvocationInitiator, IInvocationAcceptor> initiatorStep:
                    await ExecuteInitiatorStep(initiatorStep);
                    break;

                case ProtocolStep<InvocationSignal, IInvocationAcceptor, IInvocationInitiator> acceptorStep:
                    await AwaitAcceptorStep(acceptorStep);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown protocol step type: {step.GetType().Name}");
            }
        }
    }

    private async Task ExecuteInitiatorStep(
        ProtocolStep<InvocationSignal, IInvocationInitiator, IInvocationAcceptor> step)
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
                $"Invocation protocol must use ring buffer, got: {step.CommunicationMethod}");
        }

        await _initiator.SendSignalViaRingBuffer(step.Signal);
    }

    private async Task AwaitAcceptorStep(
        ProtocolStep<InvocationSignal, IInvocationAcceptor, IInvocationInitiator> step)
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
                $"Invocation protocol must use ring buffer, got: {step.CommunicationMethod}");
        }

        await _initiator.AwaitResponseFromResponderViaRingBuffer(step.Signal);
    }
}

namespace Backend.Plugins.Communication.Protocols.Invocation;

/// <summary>
/// Executes the invocation protocol from the plugin's perspective as the acceptor.
/// Walks the shared protocol definition in order and invokes the acceptor operations.
/// </summary>
public class InvocationAcceptorSteps
{
    private readonly IInvocationAcceptor _acceptor;
    private readonly IReadOnlyList<IProtocolStep<InvocationSignal>> _steps;

    public InvocationAcceptorSteps(IInvocationAcceptor acceptor)
    {
        _acceptor = acceptor ?? throw new ArgumentNullException(nameof(acceptor));
        _steps = InvocationProtocol.Definition.Steps;
    }

    public async Task Perform()
    {
        foreach (var step in _steps)
        {
            switch (step)
            {
                case ProtocolStep<InvocationSignal, IInvocationAcceptor, IInvocationInitiator> acceptorStep:
                    await ExecuteAcceptorStep(acceptorStep);
                    break;

                case ProtocolStep<InvocationSignal, IInvocationInitiator, IInvocationAcceptor> initiatorStep:
                    await AwaitInitiatorStep(initiatorStep);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown protocol step type: {step.GetType().Name}");
            }
        }
    }

    private async Task ExecuteAcceptorStep(
        ProtocolStep<InvocationSignal, IInvocationAcceptor, IInvocationInitiator> step)
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
                $"Invocation protocol must use ring buffer, got: {step.CommunicationMethod}");
        }

        await _acceptor.SendSignalViaRingBuffer(step.Signal);
    }

    private async Task AwaitInitiatorStep(
        ProtocolStep<InvocationSignal, IInvocationInitiator, IInvocationAcceptor> step)
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
                $"Invocation protocol must use ring buffer, got: {step.CommunicationMethod}");
        }

        await _acceptor.AwaitMessageFromInitiatorViaRingBuffer(step.Signal);
    }
}

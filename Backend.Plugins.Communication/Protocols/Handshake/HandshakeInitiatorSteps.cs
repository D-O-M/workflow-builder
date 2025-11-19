namespace Backend.Plugins.Communication.Protocols.Handshake;

/// <summary>
/// Executes the handshake from the initiator's perspective.
/// Only depends on IHandshakeInitiator, but walks the shared protocol script.
/// </summary>
public class HandshakeInitiatorSteps
{
    private readonly IHandshakeInitiator _initiator;
    private readonly IReadOnlyList<IProtocolStep<HandshakeSignal>> _steps;

    public HandshakeInitiatorSteps(IHandshakeInitiator initiator)
    {
        _initiator = initiator ?? throw new ArgumentNullException(nameof(initiator));
        _steps = HandshakeProtocol.Definition.Steps;
    }

    public async Task Perform()
    {
        foreach (var step in _steps)
        {
            switch (step)
            {
                case ProtocolStep<HandshakeSignal, IHandshakeInitiator, IHandshakeAcceptor> initiatorStep:
                    await ExecuteInitiatorStep(initiatorStep);
                    break;

                case ProtocolStep<HandshakeSignal, IHandshakeAcceptor, IHandshakeInitiator> acceptorStep:
                    await AwaitAcceptorStep(acceptorStep);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown protocol step type: {step.GetType().Name}");
            }
        }
    }

    private async Task ExecuteInitiatorStep(
        ProtocolStep<HandshakeSignal, IHandshakeInitiator, IHandshakeAcceptor> step)
    {
        if (step.SendOperation is not null)
        {
            // Invoke the strongly-typed method on the initiator.
            var op = step.SendOperation.Compile();
            await op(_initiator);
            return;
        }

        // No operation: send a bare signal via the configured channel.
        switch (step.CommunicationMethod)
        {
            case CommunicationMethod.StandardIO:
                await _initiator.SendSignalViaStandardIn(step.Signal);
                break;

            case CommunicationMethod.RingBuffer:
                await _initiator.SendSignalViaRingBuffer(step.Signal);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown communication method: {step.CommunicationMethod}");
        }
    }

    private async Task AwaitAcceptorStep(
        ProtocolStep<HandshakeSignal, IHandshakeAcceptor, IHandshakeInitiator> step)
    {
        if (step.ReceiveOperation is not null)
        {
            var op = step.ReceiveOperation.Compile();
            await op.Invoke(_initiator);
            return;
        }

        // From the initiator's perspective, when it's the acceptor's turn,
        // we just wait for the response on the appropriate channel.
        switch(step.CommunicationMethod)
        {
            case CommunicationMethod.StandardIO:
                await _initiator.AwaitResponseFromResponderViaStandardOut(step.Signal);
                break;
            case CommunicationMethod.RingBuffer:
                await _initiator.AwaitResponseFromResponderViaRingBuffer(step.Signal);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown communication method: {step.CommunicationMethod}");
        };
    }
}

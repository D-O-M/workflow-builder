namespace Backend.Plugins.Communication.Protocols.HandshakeProtocol;

/// <summary>
/// Executes the handshake from the acceptor's perspective.
/// Only depends on IHandshakeAcceptor, but walks the same protocol script.
/// </summary>
internal class HandshakeAcceptorSteps
{
    private readonly IHandshakeAcceptor _acceptor;
    private readonly IReadOnlyList<IProtocolStep<HandshakeSignal>> _steps;

    public HandshakeAcceptorSteps(IHandshakeAcceptor acceptor)
    {
        _acceptor = acceptor ?? throw new ArgumentNullException(nameof(acceptor));
        _steps = HandshakeProtocol.Definition.Steps;
    }

    public async Task Perform()
    {
        foreach (var step in _steps)
        {
            switch (step)
            {
                case ProtocolStep<HandshakeSignal, IHandshakeAcceptor> acceptorStep:
                    await ExecuteAcceptorStep(acceptorStep);
                    break;

                case ProtocolStep<HandshakeSignal, IHandshakeInitiator> initiatorStep:
                    await AwaitInitiatorStep(initiatorStep);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown protocol step type: {step.GetType().Name}");
            }
        }
    }

    private async Task ExecuteAcceptorStep(
        ProtocolStep<HandshakeSignal, IHandshakeAcceptor> step)
    {
        if (step.Operation is not null)
        {
            // Invoke the strongly-typed method on the acceptor.
            var op = step.Operation.Compile();
            await op(_acceptor);
            return;
        }

        // No operation: send a bare signal via the configured channel.
        switch (step.CommunicationMethod)
        {
            case CommunicationMethod.StandardIO:
                await _acceptor.SendSignalViaStandardOut(step.Signal);
                break;

            case CommunicationMethod.RingBuffer:
                await _acceptor.SendSignalViaRingBuffer(step.Signal);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown communication method: {step.CommunicationMethod}");
        }
    }

    private Task AwaitInitiatorStep(
        ProtocolStep<HandshakeSignal, IHandshakeInitiator> step)
    {
        // From the acceptor's perspective, when it's the initiator's turn,
        // we just wait for the message on the appropriate channel.
        return step.CommunicationMethod switch
        {
            CommunicationMethod.StandardIO
                => _acceptor.AwaitMessageFromInitiatorViaStandardIn(),
            CommunicationMethod.RingBuffer
                => _acceptor.AwaitMessageFromInitiatorViaRingBuffer(),
            _ => throw new InvalidOperationException(
                $"Unknown communication method: {step.CommunicationMethod}")
        };
    }
}

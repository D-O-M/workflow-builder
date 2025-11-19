using System.Linq.Expressions;

namespace Backend.Plugins.Communication.Protocols;

internal class ProtocolDefinition<TSignal, TInitiatorInterface, TAcceptorInterface>
    where TSignal : struct, Enum
    where TInitiatorInterface : class
    where TAcceptorInterface : class
{
    private readonly List<IProtocolStep<TSignal>> _steps = new();

    public ProtocolDefinition<TSignal, TInitiatorInterface, TAcceptorInterface>
        AddInitiatorStep(
        TSignal signal,
        CommunicationMethod communicationMethod,
        Expression<Func<TInitiatorInterface, Task>>? operation = null)
    {
        _steps.Add(new ProtocolStep<TSignal, TInitiatorInterface>(
            Negotiator.Initiator,
            signal,
            communicationMethod,
            operation));

        return this;
    }

    public ProtocolDefinition<TSignal, TInitiatorInterface, TAcceptorInterface> AddAcceptorStep(
        TSignal signal,
        CommunicationMethod communicationMethod,
        Expression<Func<TAcceptorInterface, Task>>? operation = null)
    {
        _steps.Add(new ProtocolStep<TSignal, TAcceptorInterface>(
            Negotiator.Acceptor,
            signal,
            communicationMethod,
            operation));

        return this;
    }

    public IReadOnlyList<IProtocolStep<TSignal>> Steps => _steps;
}

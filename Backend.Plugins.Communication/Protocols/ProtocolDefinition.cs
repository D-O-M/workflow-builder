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
        Expression<Func<TInitiatorInterface, Task>>? sendOperation = null,
        Expression<Func<TAcceptorInterface, Task>>? receiveOperation = null)
    {
        _steps.Add(new ProtocolStep<TSignal, TInitiatorInterface, TAcceptorInterface>(
            Negotiator.Initiator,
            signal,
            communicationMethod,
            sendOperation,
            receiveOperation));

        return this;
    }

    public ProtocolDefinition<TSignal, TInitiatorInterface, TAcceptorInterface> AddAcceptorStep(
        TSignal signal,
        CommunicationMethod communicationMethod,
        Expression<Func<TAcceptorInterface, Task>>? sendOperation = null,
        Expression<Func<TInitiatorInterface, Task>>? receiveOperation = null)
    {
        _steps.Add(new ProtocolStep<TSignal, TAcceptorInterface, TInitiatorInterface>(
            Negotiator.Acceptor,
            signal,
            communicationMethod,
            sendOperation,
            receiveOperation));

        return this;
    }

    public IReadOnlyList<IProtocolStep<TSignal>> Steps => _steps;
}

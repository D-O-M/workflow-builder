using System.Linq.Expressions;

namespace Backend.Plugins.Communication.Protocols;

/// <summary>
/// A single protocol step for a specific actor type (initiator or acceptor).
/// Operation is an expression that selects the method to call on that actor.
/// </summary>
internal record ProtocolStep<TSignal, TActor>(
    Negotiator Negotiator,
    TSignal Signal,
    CommunicationMethod CommunicationMethod,
    Expression<Func<TActor, Task>>? Operation = null)
    : IProtocolStep<TSignal>
    where TSignal : struct, Enum
    where TActor : class;

using System.Linq.Expressions;
using System.Numerics;

namespace Backend.Plugins.Communication.Protocols;

/// <summary>
/// A single protocol step for a specific actor type (initiator or acceptor).
/// Operation is an expression that selects the method to call on that actor.
/// </summary>
internal record ProtocolStep<TSignal, TActor1, TActor2>(
    Negotiator Negotiator,
    TSignal Signal,
    CommunicationMethod CommunicationMethod,
    Expression<Func<TActor1, Task>>? SendOperation = null,
    Expression<Func<TActor2, Task>>? ReceiveOperation = null)
    : IProtocolStep<TSignal>
    where TSignal : struct, Enum
    where TActor1 : class
    where TActor2 : class;

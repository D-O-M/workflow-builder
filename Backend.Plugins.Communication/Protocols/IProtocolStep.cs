namespace Backend.Plugins.Communication.Protocols;

/// <summary>
/// Non-generic protocol step interface so we can store steps in a single collection.
/// </summary>
internal interface IProtocolStep<TSignal> where TSignal : struct, Enum
{
    Negotiator Negotiator { get; }
    TSignal Signal { get; }
    CommunicationMethod CommunicationMethod { get; }
}

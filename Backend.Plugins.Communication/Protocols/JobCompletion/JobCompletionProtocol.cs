using Backend.Plugins.Communication.Protocols;

namespace Backend.Plugins.Communication.Protocols.JobCompletion;

/// <summary>
/// Coordinates how a completed job and its outputs are communicated between endpoints.
/// </summary>
internal static class JobCompletionProtocol
{
    public static ProtocolDefinition<JobCompletionSignal, IJobCompletionInitiator, IJobCompletionAcceptor>
        Definition { get; } = Build();

    private static ProtocolDefinition<JobCompletionSignal, IJobCompletionInitiator, IJobCompletionAcceptor> Build()
    {
        var protocol = new ProtocolDefinition<JobCompletionSignal, IJobCompletionInitiator, IJobCompletionAcceptor>();

        protocol
            .AddInitiatorStep(
                JobCompletionSignal.JobCompleted,
                CommunicationMethod.RingBuffer,
                i => i.SendJobCompleted(),
                a => a.ReceiveJobCompleted())
            .AddAcceptorStep(
                JobCompletionSignal.JobCompletedAcknowledged,
                CommunicationMethod.RingBuffer,
                a => a.SendJobCompletedAcknowledgement(),
                i => i.AwaitJobCompletedAcknowledgement())
            .AddInitiatorStep(
                JobCompletionSignal.OutputChannel,
                CommunicationMethod.RingBuffer,
                i => i.SendOutputChannelsSequentially(),
                a => a.ReceiveOutputChannelsSequentially())
            .AddInitiatorStep(
                JobCompletionSignal.OutputsComplete,
                CommunicationMethod.RingBuffer,
                i => i.SendOutputsComplete(),
                a => a.ReceiveOutputsComplete());

        return protocol;
    }
}

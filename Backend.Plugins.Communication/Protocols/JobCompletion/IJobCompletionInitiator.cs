namespace Backend.Plugins.Communication.Protocols.JobCompletion;

public interface IJobCompletionInitiator
{
    /// <summary>
    /// Sends a bare job completion signal over the ring buffer.
    /// </summary>
    Task SendSignalViaRingBuffer(JobCompletionSignal signal);

    /// <summary>
    /// Awaits a bare signal response from the acceptor over the ring buffer.
    /// </summary>
    Task AwaitResponseFromResponderViaRingBuffer(JobCompletionSignal signal);

    /// <summary>
    /// Announces job completion to the acceptor.
    /// </summary>
    Task SendJobCompleted();

    /// <summary>
    /// Waits for the acceptor to acknowledge the completion notification.
    /// </summary>
    Task AwaitJobCompletedAcknowledgement();

    /// <summary>
    /// Streams each output channel in order so the acceptor can process them sequentially.
    /// </summary>
    Task SendOutputChannelsSequentially();

    /// <summary>
    /// Sends a final signal indicating no more outputs will be transmitted.
    /// </summary>
    Task SendOutputsComplete();
}

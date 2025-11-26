namespace Backend.Plugins.Communication.Protocols.JobCompletion;

public interface IJobCompletionAcceptor
{
    /// <summary>
    /// Waits for the initiator to send a bare job completion signal over the ring buffer.
    /// </summary>
    Task ReceiveSignalFromInitiatorViaRingBuffer(JobCompletionSignal signal);

    Task AwaitMessageFromInitiatorViaRingBuffer(JobCompletionSignal signal);

    /// <summary>
    /// Waits for the initiator to send a protocol step over the ring buffer.
    /// </summary>
    Task AwaitMessageFromInitiatorViaRingBuffer(JobCompletionSignal signal);

    /// <summary>
    /// Sends a bare job completion signal over the ring buffer back to the initiator.
    /// </summary>
    Task SendSignalViaRingBuffer(JobCompletionSignal signal);

    /// <summary>
    /// Waits for the initiator to declare that the job has completed.
    /// </summary>
    Task ReceiveJobCompleted();

    /// <summary>
    /// Acknowledges readiness to accept output channel data.
    /// </summary>
    Task SendJobCompletedAcknowledgement();

    /// <summary>
    /// Receives output channel payloads in the order they are transmitted.
    /// </summary>
    Task ReceiveOutputChannelsSequentially();

    /// <summary>
    /// Confirms that all outputs have been received and processed.
    /// </summary>
    Task ReceiveOutputsComplete();
}

namespace Backend.Plugins.Communication.Protocols.JobCompletion;

public enum JobCompletionSignal : byte
{
    /// <summary>
    /// Initiator signals that job execution has finished.
    /// </summary>
    JobCompleted,

    /// <summary>
    /// Acceptor acknowledges it is ready to receive outputs.
    /// </summary>
    JobCompletedAcknowledged,

    /// <summary>
    /// Initiator streams one or more output channel payloads sequentially.
    /// </summary>
    OutputChannel,

    /// <summary>
    /// Initiator signals that all output channels have been transmitted.
    /// </summary>
    OutputsComplete
}

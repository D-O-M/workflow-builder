using Backend.Plugins.Communication;
using Backend.Plugins.Communication.Protocols;
using Backend.Plugins.Communication.Protocols.JobCompletion;

namespace Backend.Plugins.Harness.ProtocolHandlers;

internal sealed class JobCompletionInitiator : IJobCompletionInitiator
{
    private readonly ISignalChannel _channel;
    private readonly IReadOnlyCollection<SignalAction> _outputChannels;
    private readonly CancellationToken _cancellationToken;

    public JobCompletionInitiator(
        ISignalChannel channel,
        IEnumerable<SignalAction> outputChannels,
        CancellationToken cancellationToken)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _outputChannels = outputChannels?.ToArray() ?? throw new ArgumentNullException(nameof(outputChannels));
        _cancellationToken = cancellationToken;
    }

    public Task SendSignalViaRingBuffer(JobCompletionSignal signal)
        => SendSignalAsync(signal);

    public Task AwaitResponseFromAcceptorViaRingBuffer(JobCompletionSignal signal)
        => AwaitSignalAsync(signal);

    public Task SendJobCompleted()
        => SendSignalAsync(JobCompletionSignal.JobCompleted);

    public Task AwaitJobCompletedAcknowledgement()
        => AwaitSignalAsync(JobCompletionSignal.JobCompletedAcknowledged);

    public async Task SendOutputChannelsSequentially()
    {
        foreach (var action in _outputChannels)
        {
            var payload = SignalActionSerializer.Serialize(action);
            var envelope = new SignalWithPayloadEnvelope<JobCompletionSignal>(
                JobCompletionSignal.OutputChannel,
                payload.ToArray());

            await SendAsync(envelope.ToBytes());
        }
    }

    public Task SendOutputsComplete()
        => SendSignalAsync(JobCompletionSignal.OutputsComplete);

    private async Task SendSignalAsync(JobCompletionSignal signal)
    {
        var envelope = new SignalEnvelope<JobCompletionSignal>(signal).ToBytes();
        await SendAsync(envelope);
    }

    private async Task AwaitSignalAsync(JobCompletionSignal expectedSignal)
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            if (_channel.TryReceive(out var message))
            {
                if (message.Command is not SignalCommand.Dispatch)
                {
                    throw new InvalidOperationException($"Unexpected command {message.Command} encountered during job completion.");
                }

                var envelope = SignalEnvelope.FromBytes<JobCompletionSignal>(message.Payload.ToArray());
                if (envelope.Signal != expectedSignal)
                {
                    throw new ArgumentOutOfRangeException(nameof(envelope.Signal));
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), _cancellationToken);
        }

        _cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task SendAsync(ReadOnlyMemory<byte> envelope)
    {
        var message = new SignalMessage(SignalCommand.Dispatch, envelope);

        while (!_cancellationToken.IsCancellationRequested)
        {
            if (_channel.TrySend(message))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), _cancellationToken);
        }

        _cancellationToken.ThrowIfCancellationRequested();
    }
}

using Backend.Plugins.Communication;
using Backend.Plugins.Communication.Protocols.JobCompletion;

namespace Backend.Engine;

internal sealed class JobCompletionAcceptor : IJobCompletionAcceptor
{
    private readonly ISignalChannel _channel;
    private readonly CancellationToken _cancellationToken;
    private readonly List<SignalAction> _receivedOutputs = new();
    private bool _outputsCompleteReceived;

    public JobCompletionAcceptor(ISignalChannel channel, CancellationToken cancellationToken)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _cancellationToken = cancellationToken;
    }

    public IReadOnlyCollection<SignalAction> ReceivedOutputs => _receivedOutputs.AsReadOnly();

    public Task ReceiveSignalFromInitiatorViaRingBuffer(JobCompletionSignal signal)
        => AwaitSignalAsync(signal);

    public Task SendSignalViaRingBuffer(JobCompletionSignal signal)
        => SendSignalAsync(signal);

    public Task ReceiveJobCompleted()
        => AwaitSignalAsync(JobCompletionSignal.JobCompleted);

    public Task SendJobCompletedAcknowledgement()
        => SendSignalAsync(JobCompletionSignal.JobCompletedAcknowledged);

    public async Task ReceiveOutputChannelsSequentially()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            if (_channel.TryReceive(out var message))
            {
                if (message.Command is not SignalCommand.Dispatch)
                {
                    throw new InvalidOperationException($"Unexpected command {message.Command} encountered during job completion receive.");
                }

                if (message.Payload.Length == 1)
                {
                    var envelope = SignalEnvelope.FromBytes<JobCompletionSignal>(message.Payload.ToArray());
                    if (envelope.Signal == JobCompletionSignal.OutputsComplete)
                    {
                        _outputsCompleteReceived = true;
                        return;
                    }

                    throw new ArgumentOutOfRangeException(nameof(envelope.Signal));
                }

                var payloadEnvelope = SignalWithPayloadEnvelope
                    .FromBytes<JobCompletionSignal>(message.Payload.ToArray());

                if (payloadEnvelope.Signal == JobCompletionSignal.OutputChannel)
                {
                    var action = SignalActionSerializer.Deserialize(payloadEnvelope.Payload);
                    _receivedOutputs.Add(action);
                    continue;
                }

                if (payloadEnvelope.Signal == JobCompletionSignal.OutputsComplete)
                {
                    _outputsCompleteReceived = true;
                    return;
                }

                throw new ArgumentOutOfRangeException(nameof(payloadEnvelope.Signal));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), _cancellationToken);
        }

        _cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task ReceiveOutputsComplete()
    {
        if (_outputsCompleteReceived)
        {
            _outputsCompleteReceived = false;
            return;
        }

        await AwaitSignalAsync(JobCompletionSignal.OutputsComplete);
    }

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
                    throw new InvalidOperationException($"Unexpected command {message.Command} encountered during job completion receive.");
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

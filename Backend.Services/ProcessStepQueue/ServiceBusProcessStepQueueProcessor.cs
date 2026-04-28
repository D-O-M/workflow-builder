using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;
using Microsoft.Extensions.Logging;

namespace Backend.Services.ProcessStepQueue;

public sealed class ServiceBusProcessStepQueueProcessor
    : IProcessStepQueueProcessor,
      IAsyncDisposable
{
    private readonly ServiceBusProcessor _processor;
    private readonly ILogger<ServiceBusProcessStepQueueProcessor> _logger;
    private Func<IProcessStepQueueMessage, CancellationToken, Task>? _processMessageAsync;

    public ServiceBusProcessStepQueueProcessor(
        ServiceBusProcessor processor,
        ILogger<ServiceBusProcessStepQueueProcessor> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    public async Task StartProcessingAsync(
        Func<IProcessStepQueueMessage, CancellationToken, Task> processMessageAsync,
        CancellationToken cancellationToken)
    {
        if (_processMessageAsync is not null)
        {
            throw new InvalidOperationException("Process-step queue processor is already running.");
        }

        _processMessageAsync = processMessageAsync;
        _processor.ProcessMessageAsync += OnProcessMessageAsync;
        _processor.ProcessErrorAsync += OnProcessErrorAsync;
        await _processor.StartProcessingAsync(cancellationToken);
    }

    public async Task StopProcessingAsync(CancellationToken cancellationToken)
    {
        await _processor.StopProcessingAsync(cancellationToken);
        _processor.ProcessMessageAsync -= OnProcessMessageAsync;
        _processor.ProcessErrorAsync -= OnProcessErrorAsync;
        _processMessageAsync = null;
    }

    public ValueTask DisposeAsync() => _processor.DisposeAsync();

    private Task OnProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var handler = _processMessageAsync
            ?? throw new InvalidOperationException("Process-step message handler has not been configured.");

        var message = new ServiceBusProcessStepQueueMessage(args);
        return handler(message, args.CancellationToken);
    }

    private Task OnProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(
            args.Exception,
            "Process-step queue processor error from {ErrorSource} on {EntityPath}.",
            args.ErrorSource,
            args.EntityPath);
        return Task.CompletedTask;
    }
}

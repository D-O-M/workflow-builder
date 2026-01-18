using System.Text.Json;
using System.Threading.Channels;
using Backend.Interfaces.Repositories;
using Backend.Plugins.Harness;
using Backend.Plugins.Communication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed class EventHandler<TPlugin> : BackgroundService
    where TPlugin : class, IPlugin
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PluginStateConfiguration<TPlugin> _stateConfiguration;
    private readonly ILogger<EventHandler<TPlugin>> _logger;

    private readonly Channel<InvocationEnvelope> _queue = Channel.CreateUnbounded<InvocationEnvelope>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true // OK if exactly one stdin reader task writes
        });

    public EventHandler(
        IServiceScopeFactory scopeFactory,
        PluginStateConfiguration<TPlugin> stateConfiguration,
        ILogger<EventHandler<TPlugin>> logger)
    {
        _scopeFactory = scopeFactory;
        _stateConfiguration = stateConfiguration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start producer
        _ = Task.Run(() => ReadStdinLoop(stoppingToken), stoppingToken);

        // Consumer: awaits asynchronously; does not block a thread when idle
        await foreach (var envelope in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var plugin = ActivatorUtilities.CreateInstance<TPlugin>(scope.ServiceProvider);

            await PopulateStateAsync(scope.ServiceProvider, plugin, envelope, stoppingToken);

            // Breakpoint here; easy to step into Execute()
            await plugin.Execute();
        }
    }

    private async Task ReadStdinLoop(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync().ConfigureAwait(false);

                if (line is null)
                {
                    // EOF (pipe/file ended). This is terminal for stdin.
                    _queue.Writer.TryComplete();
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                InvocationEnvelope envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<InvocationEnvelope>(line)
                        ?? throw new InvalidOperationException("Invocation envelope payload missing.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse invocation envelope from stdin.");
                    continue;
                }

                await _queue.Writer.WriteAsync(envelope, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _queue.Writer.TryComplete(ex);
        }
    }

    private async Task PopulateStateAsync(
        IServiceProvider services,
        TPlugin plugin,
        InvocationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!_stateConfiguration.HasBindings)
            return;

        var repository = services.GetService<IWorkflowStateRepository>();
        if (repository is null)
        {
            _logger.LogDebug("No workflow state repository registered; skipping state hydration.");
            return;
        }

        var workflowState = await repository.GetWorkflowStateById(envelope.WorkflowInstanceId);

        if (_stateConfiguration.GlobalState is not null && workflowState.GlobalStateDataStream is not null)
        {
            var globalStateData = await ReadStateAsync(workflowState.GlobalStateDataStream, cancellationToken);
            if (!globalStateData.IsEmpty)
            {
                var globalState = _stateConfiguration.GlobalState.Deserialize(globalStateData);
                _stateConfiguration.GlobalState.Setter(plugin, globalState);
            }
        }

        if (_stateConfiguration.InputChannels.TryGetValue(envelope.InputChannelName, out var inputBinding))
        {
            var taskState = workflowState.Tasks
                .FirstOrDefault(task => task.WorkflowTaskInstanceId == envelope.WorkflowTaskInstanceId);

            if (taskState?.InputChannelStateDataStream is not null)
            {
                var inputStateData = await ReadStateAsync(taskState.InputChannelStateDataStream, cancellationToken);
                if (!inputStateData.IsEmpty)
                {
                    var inputState = inputBinding.Deserialize(inputStateData);
                    inputBinding.Setter(plugin, inputState);
                }
            }
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadStateAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}

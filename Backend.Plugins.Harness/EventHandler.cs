using System.Text.Json;
using System.Threading.Channels;
using Backend.Plugins.Communication;
using Backend.Plugins.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed class EventHandler<TPlugin> : BackgroundService
    where TPlugin : class, IPlugin
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EventHandler<TPlugin>> _logger;

    private readonly Channel<InvocationEnvelope> _queue = Channel.CreateUnbounded<InvocationEnvelope>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

    public EventHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<EventHandler<TPlugin>> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting plugin event handler for {PluginType}.", typeof(TPlugin).FullName);
        Console.Out.WriteLine("Ready");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = Task.Run(() => ReadStdinLoop(stoppingToken), stoppingToken);

        await foreach (var envelope in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var plugin = ActivatorUtilities.CreateInstance<TPlugin>(scope.ServiceProvider);

            await PopulateStateAsync(scope.ServiceProvider, plugin, envelope, stoppingToken);

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
                    _queue.Writer.TryComplete();
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                InvocationEnvelope envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<InvocationEnvelope>(line, JsonSerializerOptions.Web)
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

    private static async Task PopulateStateAsync(
        IServiceProvider services,
        TPlugin plugin,
        InvocationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var steps = services.GetServices<IPluginHydrationStep<TPlugin>>();

        foreach (var step in steps)
        {
            await step.HydrateAsync(plugin, envelope, cancellationToken).ConfigureAwait(false);
        }
    }
}

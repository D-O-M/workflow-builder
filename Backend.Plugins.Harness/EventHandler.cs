using System.Threading.Channels;
using Backend.Plugins.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal sealed class EventHandler<TPlugin> : BackgroundService
    where TPlugin : class, IPlugin
{
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true // OK if exactly one stdin reader task writes
        });

    public EventHandler(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start producer
        _ = Task.Run(() => ReadStdinLoop(stoppingToken), stoppingToken);

        // Consumer: awaits asynchronously; does not block a thread when idle
        await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var plugin = ActivatorUtilities.CreateInstance<TPlugin>(scope.ServiceProvider);

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

                await _queue.Writer.WriteAsync(line, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _queue.Writer.TryComplete(ex);
        }
    }
}

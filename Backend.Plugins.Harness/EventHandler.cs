using Backend.Plugins.Communication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Plugins.Harness;

internal sealed class EventHandler<TPlugin> : BackgroundService
    where TPlugin : class, IPlugin
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EventHandler<TPlugin>> _logger;

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
        while (!stoppingToken.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            InvocationEnvelope envelope;
            try
            {
                envelope = InvocationEnvelopeSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(line));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse invocation envelope from stdin.");
                continue;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var plugin = ActivatorUtilities.CreateInstance<TPlugin>(scope.ServiceProvider);
            var resultFactory = scope.ServiceProvider.GetRequiredService<PluginExecutionResultFactory<TPlugin>>();

            var startedAt = DateTimeOffset.UtcNow;

            try
            {
                await PopulateStateAsync(scope.ServiceProvider, plugin, envelope, stoppingToken);
                await plugin.Execute();

                var completedAt = DateTimeOffset.UtcNow;
                Console.Out.WriteLine(
                    PluginExecutionResultSerializer.Serialize(
                        resultFactory.CreateSuccess(plugin, envelope, startedAt, completedAt)));
            }
            catch (Exception ex)
            {
                var completedAt = DateTimeOffset.UtcNow;
                _logger.LogError(ex, "Plugin execution failed for workflow task {WorkflowTaskInstanceId}.", envelope.WorkflowTaskInstanceId);
                Console.Out.WriteLine(
                    PluginExecutionResultSerializer.Serialize(
                        resultFactory.CreateFailure(plugin, envelope, startedAt, completedAt, ex)));
            }
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

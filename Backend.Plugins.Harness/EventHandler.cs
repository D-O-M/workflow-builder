using Backend.Plugins.Communication;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Plugins.Harness;

internal sealed class EventHandler<TPlugin> : BackgroundService
    where TPlugin : class, IPlugin
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EventHandler<TPlugin>> _logger;
    private readonly bool _diagnosticsEnabled;
    private readonly TimeSpan? _heartbeatInterval;
    private readonly SemaphoreSlim _outputGate = new(1, 1);
    private PluginProcessProtocolWriter? _protocolWriter;

    public EventHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<EventHandler<TPlugin>> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _diagnosticsEnabled = string.Equals(
            Environment.GetEnvironmentVariable("PluginRuntime__Diagnostics"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        _heartbeatInterval = ReadHeartbeatInterval();
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting plugin event handler for {PluginType} with reactive invocation processing.",
            typeof(TPlugin).FullName);
        EnsureProtocolWriter();
        await _protocolWriter!.WriteReadyAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureProtocolWriter();
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeatTask = _heartbeatInterval is TimeSpan interval
            ? RunHeartbeatAsync(interval, heartbeatCancellation.Token)
            : Task.CompletedTask;
        var inFlight = new List<Task>();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                inFlight.RemoveAll(task => task.IsCompleted);
                inFlight.Add(ProcessInvocationSlotAsync(line, stoppingToken));
            }

            if (inFlight.Count > 0)
            {
                await Task.WhenAll(inFlight).ConfigureAwait(false);
            }
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }
    }

    private async Task ProcessInvocationSlotAsync(string line, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessInvocationAsync(line, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled plugin invocation failure for {PluginType}.", typeof(TPlugin).FullName);
            await WriteErrorAsync(
                $"Unhandled plugin invocation failure for {typeof(TPlugin).FullName}.",
                ex,
                jobId: null,
                stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessInvocationAsync(string line, CancellationToken stoppingToken)
    {
        InvocationEnvelope envelope;
        try
        {
            WriteDiagnostic($"Received invocation payload for {typeof(TPlugin).Name}.");
            envelope = InvocationEnvelopeSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(line));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse invocation envelope from stdin.");
            await WriteErrorAsync(
                "Failed to parse invocation envelope from stdin.",
                ex,
                jobId: null,
                stoppingToken).ConfigureAwait(false);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var plugin = ActivatorUtilities.CreateInstance<TPlugin>(scope.ServiceProvider);
        var resultFactory = scope.ServiceProvider.GetRequiredService<PluginExecutionResultFactory<TPlugin>>();
        var hydrationContext = new PluginHydrationContext();

        var startedAt = DateTimeOffset.UtcNow;
        using var loggerScope = _logger.BeginScope(
            PluginInvocationTelemetry.BuildExecutionScope(envelope, envelope.PluginName));
        using var activity = PluginInvocationTelemetry.StartExecutionActivity(envelope, envelope.PluginName);

        try
        {
            WriteDiagnostic(
                $"Starting plugin invocation for process {envelope.ProcessId} step {envelope.ProcessStepId}.");
            _logger.LogInformation(
                "Plugin invocation started for process {ProcessId} task {ProcessStepId}.",
                envelope.ProcessId,
                envelope.ProcessStepId);
            WriteDiagnostic($"Hydrating plugin state for step {envelope.ProcessStepId}.");
            var hydrationStopwatch = Stopwatch.StartNew();
            await PopulateStateAsync(scope.ServiceProvider, plugin, envelope, hydrationContext, stoppingToken);
            hydrationStopwatch.Stop();
            WriteDiagnostic($"Executing plugin for step {envelope.ProcessStepId}.");
            var executeStopwatch = Stopwatch.StartNew();
            await plugin.Execute();
            executeStopwatch.Stop();

            var completedAt = DateTimeOffset.UtcNow;
            var timingsMs = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["hydrationMs"] = hydrationStopwatch.Elapsed.TotalMilliseconds,
                ["executeMs"] = executeStopwatch.Elapsed.TotalMilliseconds
            };
            WriteDiagnostic($"Plugin execution completed for step {envelope.ProcessStepId}.");
            _logger.LogInformation(
                "Plugin invocation completed for process {ProcessId} task {ProcessStepId} in {DurationMs}ms. Hydration={HydrationMs}ms Execute={ExecuteMs}ms.",
                envelope.ProcessId,
                envelope.ProcessStepId,
                (completedAt - startedAt).TotalMilliseconds,
                timingsMs["hydrationMs"],
                timingsMs["executeMs"]);
            await WriteResultAsync(
                resultFactory.CreateSuccess(
                    plugin,
                    envelope,
                    startedAt,
                    completedAt,
                    timingsMs,
                    hydrationContext.EffectiveInputData),
                stoppingToken);
        }
        catch (Exception ex)
        {
            var completedAt = DateTimeOffset.UtcNow;
            WriteDiagnostic(
                $"Plugin execution failed for step {envelope.ProcessStepId}: {ex.GetType().Name}: {ex.Message}");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(
                ex,
                "Plugin execution failed for process {ProcessId} task {ProcessStepId}.",
                envelope.ProcessId,
                envelope.ProcessStepId);
            await WriteResultAsync(
                resultFactory.CreateFailure(
                    plugin,
                    envelope,
                    startedAt,
                    completedAt,
                    ex,
                    hydrationContext.EffectiveInputData),
                stoppingToken);
        }
    }

    private async Task WriteResultAsync(
        PluginExecutionResult result,
        CancellationToken cancellationToken)
    {
        EnsureProtocolWriter();
        await _protocolWriter!.WriteResultAsync(result, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteErrorAsync(
        string message,
        Exception exception,
        Guid? jobId,
        CancellationToken cancellationToken)
    {
        EnsureProtocolWriter();
        await _protocolWriter!.WriteErrorAsync(message, exception, jobId, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunHeartbeatAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await _protocolWriter!.WriteHeartbeatAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureProtocolWriter()
    {
        if (_protocolWriter is not null)
        {
            return;
        }

        var protocolWriter = new PluginProcessProtocolWriter(Console.Out, _outputGate);
        _protocolWriter = protocolWriter;
        Console.SetOut(new PluginProcessLogTextWriter(protocolWriter));
    }

    private static async Task PopulateStateAsync(
        IServiceProvider services,
        TPlugin plugin,
        InvocationEnvelope envelope,
        PluginHydrationContext context,
        CancellationToken cancellationToken)
    {
        var steps = services.GetServices<IPluginHydrationStep<TPlugin>>();

        foreach (var step in steps)
        {
            await step.HydrateAsync(plugin, envelope, context, cancellationToken).ConfigureAwait(false);
        }
    }

    private void WriteDiagnostic(string message)
    {
        if (!_diagnosticsEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[plugin-harness] {DateTimeOffset.UtcNow:O} {message}");
    }

    private static TimeSpan? ReadHeartbeatInterval()
    {
        var configuredValue = Environment.GetEnvironmentVariable("PluginRuntime__HeartbeatIntervalSeconds");
        if (!int.TryParse(configuredValue, out var seconds) || seconds <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}

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
                WriteDiagnostic($"Received invocation payload for {typeof(TPlugin).Name}.");
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
            using var loggerScope = _logger.BeginScope(BuildExecutionScope(envelope, envelope.PluginName));
            using var activity = StartExecutionActivity(envelope, envelope.PluginName);

            try
            {
                WriteDiagnostic(
                    $"Starting plugin invocation for process {envelope.ProcessId} step {envelope.ProcessStepId}.");
                _logger.LogInformation(
                    "Plugin invocation started for process {ProcessId} task {ProcessStepId}.",
                    envelope.ProcessId,
                    envelope.ProcessStepId);
                WriteDiagnostic($"Hydrating plugin state for step {envelope.ProcessStepId}.");
                await PopulateStateAsync(scope.ServiceProvider, plugin, envelope, stoppingToken);
                WriteDiagnostic($"Executing plugin for step {envelope.ProcessStepId}.");
                await plugin.Execute();

                var completedAt = DateTimeOffset.UtcNow;
                WriteDiagnostic($"Plugin execution completed for step {envelope.ProcessStepId}.");
                _logger.LogInformation(
                    "Plugin invocation completed for process {ProcessId} task {ProcessStepId} in {DurationMs}ms.",
                    envelope.ProcessId,
                    envelope.ProcessStepId,
                    (completedAt - startedAt).TotalMilliseconds);
                Console.Out.WriteLine(
                    PluginExecutionResultSerializer.Serialize(
                        resultFactory.CreateSuccess(plugin, envelope, startedAt, completedAt)));
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

    private static Activity? StartExecutionActivity(InvocationEnvelope envelope, string pluginName)
    {
        var activity = PluginExecutionTelemetry.ActivitySource.StartActivity(
            "plugin.execute",
            ActivityKind.Internal);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag("workflow.instance_id", envelope.ProcessId.ToString());
        activity.SetTag("workflow.task_instance_id", envelope.ProcessStepId.ToString());
        activity.SetTag("blueprint.definition_id", envelope.BlueprintDefinitionId);
        activity.SetTag("blueprint.version_id", envelope.ExecutableBlueprintId);
        activity.SetTag("blueprint.task_id", envelope.BlueprintNodeId.ToString());
        activity.SetTag("task.definition_id", envelope.TaskDefinitionId);
        activity.SetTag("task.version_id", envelope.TaskVersionId);
        activity.SetTag("task.version", envelope.TaskVersion);
        activity.SetTag("plugin.name", pluginName);
        activity.SetTag("event.name", "plugin.execute");

        return activity;
    }

    private static Dictionary<string, object?> BuildExecutionScope(InvocationEnvelope envelope, string pluginName) =>
        new(StringComparer.Ordinal)
        {
            ["workflow.instance_id"] = envelope.ProcessId,
            ["workflow.task_instance_id"] = envelope.ProcessStepId,
            ["blueprint.definition_id"] = envelope.BlueprintDefinitionId,
            ["blueprint.version_id"] = envelope.ExecutableBlueprintId,
            ["blueprint.task_id"] = envelope.BlueprintNodeId,
            ["task.definition_id"] = envelope.TaskDefinitionId,
            ["task.version_id"] = envelope.TaskVersionId,
            ["task.version"] = envelope.TaskVersion,
            ["plugin.name"] = pluginName,
            ["event.name"] = "plugin.execute"
        };

    private void WriteDiagnostic(string message)
    {
        if (!_diagnosticsEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[plugin-harness] {DateTimeOffset.UtcNow:O} {message}");
    }
}

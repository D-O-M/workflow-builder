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
            using var loggerScope = _logger.BeginScope(BuildExecutionScope(envelope, envelope.PluginName));
            using var activity = StartExecutionActivity(envelope, envelope.PluginName);

            try
            {
                _logger.LogInformation(
                    "Plugin invocation started for workflow instance {WorkflowInstanceId} task {WorkflowTaskInstanceId}.",
                    envelope.WorkflowInstanceId,
                    envelope.WorkflowTaskInstanceId);
                await PopulateStateAsync(scope.ServiceProvider, plugin, envelope, stoppingToken);
                await plugin.Execute();

                var completedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation(
                    "Plugin invocation completed for workflow instance {WorkflowInstanceId} task {WorkflowTaskInstanceId} in {DurationMs}ms.",
                    envelope.WorkflowInstanceId,
                    envelope.WorkflowTaskInstanceId,
                    (completedAt - startedAt).TotalMilliseconds);
                Console.Out.WriteLine(
                    PluginExecutionResultSerializer.Serialize(
                        resultFactory.CreateSuccess(plugin, envelope, startedAt, completedAt)));
            }
            catch (Exception ex)
            {
                var completedAt = DateTimeOffset.UtcNow;
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(
                    ex,
                    "Plugin execution failed for workflow instance {WorkflowInstanceId} task {WorkflowTaskInstanceId}.",
                    envelope.WorkflowInstanceId,
                    envelope.WorkflowTaskInstanceId);
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

        activity.SetTag("workflow.instance_id", envelope.WorkflowInstanceId.ToString());
        activity.SetTag("workflow.task_instance_id", envelope.WorkflowTaskInstanceId.ToString());
        activity.SetTag("blueprint.definition_id", envelope.BlueprintDefinitionId);
        activity.SetTag("blueprint.version_id", envelope.WorkflowBlueprintId);
        activity.SetTag("blueprint.task_id", envelope.BlueprintTaskId.ToString());
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
            ["workflow.instance_id"] = envelope.WorkflowInstanceId,
            ["workflow.task_instance_id"] = envelope.WorkflowTaskInstanceId,
            ["blueprint.definition_id"] = envelope.BlueprintDefinitionId,
            ["blueprint.version_id"] = envelope.WorkflowBlueprintId,
            ["blueprint.task_id"] = envelope.BlueprintTaskId,
            ["task.definition_id"] = envelope.TaskDefinitionId,
            ["task.version_id"] = envelope.TaskVersionId,
            ["task.version"] = envelope.TaskVersion,
            ["plugin.name"] = pluginName,
            ["event.name"] = "plugin.execute"
        };
}

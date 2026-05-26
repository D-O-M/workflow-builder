namespace Backend.Engine;

public sealed class PluginAgentHealthWorker : BackgroundService
{
    private readonly PluginAgentHealthEventBus _eventBus;
    private readonly PluginAgentHealthPolicy _policy;
    private readonly IEnumerable<IPluginAgentHealthDecisionHandler> _decisionHandlers;
    private readonly ILogger<PluginAgentHealthWorker> _logger;

    public PluginAgentHealthWorker(
        PluginAgentHealthEventBus eventBus,
        PluginAgentHealthPolicy policy,
        IEnumerable<IPluginAgentHealthDecisionHandler> decisionHandlers,
        ILogger<PluginAgentHealthWorker> logger)
    {
        _eventBus = eventBus;
        _policy = policy;
        _decisionHandlers = decisionHandlers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var healthEvent in _eventBus.ReadAllAsync(stoppingToken))
        {
            PluginAgentHealthPolicyDecision decision;
            try
            {
                decision = _policy.Evaluate(healthEvent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "Failed to evaluate plugin-agent health event {EventType} for task version {TaskVersionId}.",
                    healthEvent.EventType,
                    healthEvent.Identity.TaskVersionId);
                continue;
            }

            if (decision.HasOutcome(PluginAgentHealthPolicyOutcome.Warn))
            {
                _logger.LogWarning(
                    "Plugin-agent health policy decision for task version {TaskVersionId} ({PluginName} {PluginVersion}): {Reason}. Outcomes: {Outcomes}.",
                    decision.Event.Identity.TaskVersionId,
                    decision.Event.Identity.PluginName,
                    decision.Event.Identity.PluginVersion,
                    decision.Reason,
                    decision.Outcomes);
            }

            foreach (var handler in _decisionHandlers)
            {
                try
                {
                    await handler.ApplyPluginAgentHealthDecisionAsync(decision, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(
                        ex,
                        "Plugin-agent health decision handler {HandlerType} failed for event {EventType}.",
                        handler.GetType().FullName,
                        decision.Event.EventType);
                }
            }
        }
    }
}

using Backend.Database.Relational.Entities;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.Models;

namespace Backend.Engine;

public class NanoServiceOrchestrator : BackgroundService
{
    private readonly IAgentManager _agentManager;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly Lock _sync = new();

    private readonly Dictionary<int, NanoServiceAgent> _agents = [];
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    public NanoServiceOrchestrator(
        IAgentManager agentManager,
        ILogger<NanoServiceOrchestrator> logger,
        IServiceScopeFactory serviceScopeFactory)
    {
        _agentManager = agentManager;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReloadAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        await StopAgentsAsync(stoppingToken);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            await ReloadCoreAsync(cancellationToken);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping {OrchestratorName}.", nameof(NanoServiceOrchestrator));
        await StopAgentsAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task ReloadCoreAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var workflowTaskRepository = scope.ServiceProvider.GetRequiredService<IWorkflowTaskRepository>();

        var allVersions = await workflowTaskRepository.GetAll(cancellationToken);

        foreach (var lane in allVersions
                     .GroupBy(version => (version.TaskDefinitionId, version.Version.Major, version.Version.Minor)))
        {
            var ordered = lane
                .OrderByDescending(version => version.Version)
                .ToList();

            var active = ordered.FirstOrDefault(version => version.State == TaskVersionState.Active);
            var highestRegistered = ordered.FirstOrDefault(version => version.State == TaskVersionState.Registered);

            if (highestRegistered is not null)
            {
                if (active is null || highestRegistered.Version > active.Version)
                {
                    if (await TryStartAgentAsync(highestRegistered, scope, cancellationToken))
                    {
                        await workflowTaskRepository.PromoteVersionAsync(highestRegistered.Id, cancellationToken);
                        _logger.LogInformation(
                            "Promoted task version {TaskName} {Version} to active lane {Major}.{Minor}.",
                            highestRegistered.Name,
                            highestRegistered.Version,
                            highestRegistered.Version.Major,
                            highestRegistered.Version.Minor);
                    }
                }
                else
                {
                    foreach (var staleRegistered in ordered.Where(version => version.State == TaskVersionState.Registered))
                    {
                        await workflowTaskRepository.MarkVersionSupersededAsync(staleRegistered.Id, cancellationToken);
                    }
                }
            }
        }

        allVersions = await workflowTaskRepository.GetAll(cancellationToken);

        foreach (var version in allVersions.Where(version =>
                     version.State is TaskVersionState.Active or TaskVersionState.Draining))
        {
            await TryStartAgentAsync(version, scope, cancellationToken);
        }

        foreach (var drainingVersion in allVersions.Where(version => version.State == TaskVersionState.Draining))
        {
            await TryRetireVersionAsync(drainingVersion.Id, workflowTaskRepository, cancellationToken);
        }

        foreach (var stoppedVersion in allVersions.Where(version =>
                     version.State is TaskVersionState.Superseded or TaskVersionState.Failed))
        {
            await StopAgentIfIdleAsync(stoppedVersion.Id, cancellationToken);
        }
    }

    private async Task<bool> TryStartAgentAsync(
        WorkflowTaskInfo taskInfo,
        AsyncServiceScope scope,
        CancellationToken cancellationToken)
    {
        if (_agentManager.AgentExists(taskInfo.Id))
        {
            return true;
        }

        try
        {
            var entryPoint = await DownloadPlugin(
                taskInfo.Name,
                taskInfo.Version,
                taskInfo.Path,
                scope,
                cancellationToken);

            var agent = scope.ServiceProvider.GetRequiredService<NanoServiceAgent>();
            await agent.InitialiseAsync(
                entryPoint,
                $"{taskInfo.Name} (Version: {taskInfo.Version})",
                cancellationToken);

            lock (_sync)
            {
                if (!_agents.ContainsKey(taskInfo.Id))
                {
                    _agents.Add(taskInfo.Id, agent);
                    _agentManager.RegisterAgent(taskInfo.Id, agent);
                }
            }

            _logger.LogInformation(
                "Plugin agent ready for task version {TaskVersionId} ({TaskName} {Version}).",
                taskInfo.Id,
                taskInfo.Name,
                taskInfo.Version);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start agent for task version {TaskVersionId} ({TaskName} {Version}).",
                taskInfo.Id,
                taskInfo.Name,
                taskInfo.Version);

            var workflowTaskRepository = scope.ServiceProvider.GetRequiredService<IWorkflowTaskRepository>();
            await workflowTaskRepository.MarkVersionFailedAsync(taskInfo.Id, cancellationToken);
            return false;
        }
    }

    private async Task<FileInfo> DownloadPlugin(
        string pluginName,
        Version pluginVersion,
        string entryPoint,
        IServiceScope serviceScope,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Downloading and extracting version {PluginVersion} of plugin {PluginName}.",
            pluginVersion,
            pluginName);

        var pluginInfo = new PluginBlobInfo(
            $"{pluginName}_{pluginVersion}.zip",
            pluginName,
            pluginVersion);

        var workspaceFactory = serviceScope.ServiceProvider
            .GetRequiredService<IPluginWorkspaceFactoryUtility>();
        var workspaceDirectory = await workspaceFactory.CreateWorkspaceAsync(
            pluginName,
            pluginVersion,
            cancellationToken);

        var archiveExtractor = serviceScope.ServiceProvider
            .GetRequiredService<IPluginBundleSource>();

        await archiveExtractor.ExtractBundleAsync(
            pluginInfo,
            workspaceDirectory,
            cancellationToken);

        return new FileInfo(Path.Combine(workspaceDirectory, entryPoint));
    }

    private async Task TryRetireVersionAsync(
        int taskVersionId,
        IWorkflowTaskRepository workflowTaskRepository,
        CancellationToken cancellationToken)
    {
        if (_agentManager.GetActiveJobCount(taskVersionId) > 0)
        {
            return;
        }

        await StopAgentIfIdleAsync(taskVersionId, cancellationToken);
        await workflowTaskRepository.MarkVersionSupersededAsync(taskVersionId, cancellationToken);
    }

    private async Task StopAgentIfIdleAsync(int taskVersionId, CancellationToken cancellationToken)
    {
        if (!_agentManager.AgentExists(taskVersionId) || _agentManager.GetActiveJobCount(taskVersionId) > 0)
        {
            return;
        }

        NanoServiceAgent agent;
        lock (_sync)
        {
            if (!_agents.TryGetValue(taskVersionId, out agent!))
            {
                _agentManager.UnregisterAgent(taskVersionId);
                return;
            }

            _agents.Remove(taskVersionId);
            _agentManager.UnregisterAgent(taskVersionId);
        }

        _logger.LogInformation("Stopping drained plugin agent {AgentName}.", agent.Name);
        await agent.StopAsync(cancellationToken);
    }

    private async Task StopAgentsAsync(CancellationToken cancellationToken)
    {
        List<KeyValuePair<int, NanoServiceAgent>> agentsToStop;
        lock (_sync)
        {
            agentsToStop = [.. _agents];
            _agents.Clear();
        }

        foreach (var (taskVersionId, agent) in agentsToStop)
        {
            _agentManager.UnregisterAgent(taskVersionId);
            _logger.LogInformation("Stopping plugin agent {AgentName}.", agent.Name);
            await agent.StopAsync(cancellationToken);
        }
    }
}

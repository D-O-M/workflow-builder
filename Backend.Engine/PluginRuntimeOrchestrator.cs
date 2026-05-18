using Backend.Database.Relational.Entities;
using Backend.Engine.Capabilities;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.Models;
using Backend.PluginAbi;

namespace Backend.Engine;

public interface IPluginRuntimeReloader
{
    Task ReloadAsync(CancellationToken cancellationToken);
}

public class PluginRuntimeOrchestrator : BackgroundService, IPluginRuntimeReloader
{
    private readonly IAgentManager _agentManager;
    private readonly IPluginRuntimeCapabilityService _capabilityService;
    private readonly PluginAbiCompatibilityService _abiCompatibilityService;
    private readonly PluginRuntimeCatalog _pluginRuntimeCatalog;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly Lock _sync = new();

    private readonly Dictionary<int, PluginRuntimeAgent> _agents = [];
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    public PluginRuntimeOrchestrator(
        IAgentManager agentManager,
        IPluginRuntimeCapabilityService capabilityService,
        PluginAbiCompatibilityService abiCompatibilityService,
        PluginRuntimeCatalog pluginRuntimeCatalog,
        ILogger<PluginRuntimeOrchestrator> logger,
        IServiceScopeFactory serviceScopeFactory)
    {
        _agentManager = agentManager;
        _capabilityService = capabilityService;
        _abiCompatibilityService = abiCompatibilityService;
        _pluginRuntimeCatalog = pluginRuntimeCatalog;
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
        _logger.LogInformation("Stopping {OrchestratorName}.", nameof(PluginRuntimeOrchestrator));
        await StopAgentsAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task ReloadCoreAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var taskDefinitionRepository = scope.ServiceProvider.GetRequiredService<ITaskDefinitionRepository>();

        var allVersions = await taskDefinitionRepository.GetAll(cancellationToken);

        var desiredVersionIds = new HashSet<int>();
        var desiredRoutes = new Dictionary<PluginRuntimeLane, TaskDefinitionInfo>();

        foreach (var lane in allVersions
                     .GroupBy(version => (version.TaskDefinitionId, version.Version.Major, version.Version.Minor)))
        {
            var laneKey = new PluginRuntimeLane(
                lane.Key.TaskDefinitionId,
                lane.Key.Major,
                lane.Key.Minor);

            var ordered = lane
                .OrderByDescending(version => version.Version)
                .ToList();

            TaskDefinitionInfo? routedVersion = null;
            foreach (var candidate in ordered)
            {
                if (!IsRuntimeAbiCompatible(candidate))
                {
                    await StopAgentIfIdleAsync(candidate.Id, cancellationToken);
                    continue;
                }

                if (await TryEnsureAgentAvailableAsync(candidate, scope, cancellationToken))
                {
                    routedVersion = candidate;
                    break;
                }
            }

            if (routedVersion is null)
            {
                _logger.LogWarning(
                    "No runnable plugin version is currently available for lane {TaskDefinitionId} {Major}.{Minor}.",
                    laneKey.TaskDefinitionId,
                    laneKey.Major,
                    laneKey.Minor);
                continue;
            }

            desiredVersionIds.Add(routedVersion.Id);
            desiredRoutes[laneKey] = routedVersion;

            var routedChanged = !_pluginRuntimeCatalog.TryResolvePluginRuntime(
                    laneKey.TaskDefinitionId,
                    laneKey.Major,
                    laneKey.Minor,
                    out var previousRoute) ||
                previousRoute.TaskVersionId != routedVersion.Id;

            if (routedChanged)
            {
                _logger.LogInformation(
                    "Routing lane {TaskDefinitionId} {Major}.{Minor} to task version {TaskVersionId} ({TaskName} {Version}).",
                    laneKey.TaskDefinitionId,
                    laneKey.Major,
                    laneKey.Minor,
                    routedVersion.Id,
                    routedVersion.Name,
                    routedVersion.Version);
            }

            foreach (var staleVersion in ordered.Where(version => version.Id != routedVersion.Id))
            {
                await StopAgentIfIdleAsync(staleVersion.Id, cancellationToken);
            }
        }

        _pluginRuntimeCatalog.ReplaceRoutes(allVersions, desiredRoutes);

        List<int> staleAgentIds;
        lock (_sync)
        {
            staleAgentIds = [.. _agents.Keys.Where(taskVersionId => !desiredVersionIds.Contains(taskVersionId))];
        }

        foreach (var staleAgentId in staleAgentIds)
        {
            await StopAgentIfIdleAsync(staleAgentId, cancellationToken);
        }
    }

    private bool IsRuntimeAbiCompatible(TaskDefinitionInfo taskInfo)
    {
        var result = _abiCompatibilityService.Evaluate(taskInfo.Abi);
        if (result.IsCompatible)
        {
            return true;
        }

        _logger.LogWarning(
            "Task version {TaskVersionId} ({TaskName} {Version}) is not runnable because its plugin ABI metadata is incompatible: {Issues}",
            taskInfo.Id,
            taskInfo.Name,
            taskInfo.Version,
            string.Join("; ", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));

        return false;
    }

    private async Task<bool> TryEnsureAgentAvailableAsync(
        TaskDefinitionInfo taskInfo,
        AsyncServiceScope scope,
        CancellationToken cancellationToken)
    {
        if (_agentManager.AgentExists(taskInfo.Id))
        {
            return true;
        }

        return await TryStartAgentAsync(taskInfo, scope, cancellationToken);
    }

    private async Task<bool> TryStartAgentAsync(
        TaskDefinitionInfo taskInfo,
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

            var capabilityReport = await _capabilityService.ValidateWorkspaceAsync(
                entryPoint,
                $"{taskInfo.Name} (Version: {taskInfo.Version})",
                cancellationToken);
            if (!capabilityReport.Succeeded)
            {
                _logger.LogWarning(
                    "Task version {TaskVersionId} ({TaskName} {Version}) is not runnable because {FailureCount} required capability check(s) failed.",
                    taskInfo.Id,
                    taskInfo.Name,
                    taskInfo.Version,
                    capabilityReport.Results.Count(result => result.Status != CapabilityValidationStatus.Satisfied));
                return false;
            }

            var agent = scope.ServiceProvider.GetRequiredService<PluginRuntimeAgent>();
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

    private async Task StopAgentIfIdleAsync(int taskVersionId, CancellationToken cancellationToken)
    {
        if (!_agentManager.AgentExists(taskVersionId) || _agentManager.GetActiveStepCount(taskVersionId) > 0)
        {
            return;
        }

        PluginRuntimeAgent agent;
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
        List<KeyValuePair<int, PluginRuntimeAgent>> agentsToStop;
        lock (_sync)
        {
            agentsToStop = [.. _agents];
            _agents.Clear();
        }

        _pluginRuntimeCatalog.Clear();

        foreach (var (taskVersionId, agent) in agentsToStop)
        {
            _agentManager.UnregisterAgent(taskVersionId);
            _logger.LogInformation("Stopping plugin agent {AgentName}.", agent.Name);
            await agent.StopAsync(cancellationToken);
        }
    }
}

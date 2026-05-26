using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using Backend.Engine.Debugging;
using Backend.Plugins.Communication;
using Backend.Topology;
using CliWrap;
using CliWrap.Builders;
using CliWrap.EventStream;
using Microsoft.Extensions.Hosting;

namespace Backend.Engine;

public class PluginRuntimeAgent
{
    private const string LinuxProcessWrapperScript = "parent_pid=\"$1\"; shift; \"$@\" & child_pid=$!; trap 'kill -TERM \"$child_pid\" 2>/dev/null; wait \"$child_pid\" 2>/dev/null' EXIT INT TERM; while kill -0 \"$parent_pid\" 2>/dev/null && kill -0 \"$child_pid\" 2>/dev/null; do sleep 1; done; kill -TERM \"$child_pid\" 2>/dev/null; wait \"$child_pid\"";

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger _logger;
    private readonly PluginResultFinalizationQueue _resultFinalizationQueue;
    private readonly PluginRuntimeAgentTelemetry _agentTelemetry;
    private readonly PluginAgentHealthEventBus _healthEventBus;
    private readonly PluginAgentHealthPolicyOptions _healthOptions;
    private readonly PluginAgentExitNotificationService _exitNotificationService;
    private readonly IChildProcessTracker _childProcessTracker;
    private readonly IOperatingSystemInformation _operatingSystemInformation;
    private readonly IPluginRuntimeConfigurationService _pluginRuntimeConfigurationService;
    private readonly IAsyncDelay _asyncDelay;
    private Process? _process;
    private CancellationTokenSource? _processCancellation;
    private Pipe _commsStream = new();
    private readonly TaskCompletionSource _readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _queueWriterGate = new(1, 1);
    private Task? _runTask;
    private readonly StringBuilder _stdoutBuffer = new();
    private PluginRuntimeProtocolSession? _protocolSession;
    private int _stopRequested;
    private int _policyStopRequested;
    private int _heartbeatMissedPublished;
    private Task? _heartbeatMonitorTask;
    private PluginAgentIdentity _identity = PluginAgentIdentity.Unknown("UninitializedPlugin");

    private string _name = string.Empty;
    public string Name => _name;

    public PluginRuntimeAgent(
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        ILogger<PluginRuntimeAgent> logger,
        PluginResultFinalizationQueue resultFinalizationQueue,
        PluginRuntimeAgentTelemetry agentTelemetry,
        PluginAgentHealthEventBus healthEventBus,
        PluginAgentHealthPolicyOptions healthOptions,
        PluginAgentExitNotificationService exitNotificationService,
        IChildProcessTracker childProcessTracker,
        IOperatingSystemInformation operatingSystemInformation,
        IPluginRuntimeConfigurationService pluginRuntimeConfigurationService,
        IAsyncDelay asyncDelay)
    {
        _configuration = configuration;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
        _resultFinalizationQueue = resultFinalizationQueue;
        _agentTelemetry = agentTelemetry;
        _healthEventBus = healthEventBus;
        _healthOptions = healthOptions;
        _exitNotificationService = exitNotificationService;
        _childProcessTracker = childProcessTracker;
        _operatingSystemInformation = operatingSystemInformation;
        _pluginRuntimeConfigurationService = pluginRuntimeConfigurationService;
        _asyncDelay = asyncDelay;
    }

    public async Task InitialiseAsync(
        FileInfo entryPoint,
        PluginAgentIdentity identity,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        _identity = identity;
        _name = identity.DisplayName;
        _stopRequested = 0;
        _policyStopRequested = 0;
        _heartbeatMissedPublished = 0;

        if (_process is not null)
        {
            throw new InvalidOperationException("Plugin runtime process is already initialised.");
        }

        _protocolSession = new PluginRuntimeProtocolSession(
            _identity,
            _resultFinalizationQueue,
            _agentTelemetry,
            _healthEventBus,
            _logger);
        _runTask = Run(entryPoint, token);

        try
        {
            await WaitUntilReadyAsync(token);
            StartHeartbeatMonitor();
        }
        catch
        {
            await StopAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task Run(
        FileInfo fileInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            _processCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            await using var stdinStream = _commsStream.Reader.AsStream(leaveOpen: true);
            var cosmosConnectionName =
                _configuration[WorkflowBuilderTopology.Cosmos.ConnectionNameKey]
                ?? WorkflowBuilderTopology.Cosmos.DefaultDatabaseName;
            var cosmosConnectionString =
                LocalDevelopmentResourceSettings.GetCosmosConnectionString(
                    _configuration,
                    _hostEnvironment);
            var blobConnectionString =
                LocalDevelopmentResourceSettings.GetBlobConnectionString(
                    _configuration,
                    _hostEnvironment);
            var pluginsBlobContainerName =
                LocalDevelopmentResourceSettings.GetPluginsBlobContainerName(
                    _configuration,
                    _hostEnvironment);
            var artifactsBlobContainerName =
                LocalDevelopmentResourceSettings.GetArtifactsBlobContainerName(
                    _configuration,
                    _hostEnvironment);
            var environmentVariables = new Dictionary<string, string?>
            {
                {
                    "OTEL_SDK_DISABLED",
                    GetConfigurationOrEnvironmentValue("OTEL_SDK_DISABLED")
                },
                {
                    "OTEL_EXPORTER_OTLP_ENDPOINT",
                    GetConfigurationOrEnvironmentValue("OTEL_EXPORTER_OTLP_ENDPOINT")
                },
                {
                    "OTEL_EXPORTER_OTLP_PROTOCOL",
                    GetConfigurationOrEnvironmentValue("OTEL_EXPORTER_OTLP_PROTOCOL")
                },
                {
                    "OTEL_EXPORTER_OTLP_HEADERS",
                    GetConfigurationOrEnvironmentValue("OTEL_EXPORTER_OTLP_HEADERS")
                },
                {
                    "OTEL_RESOURCE_ATTRIBUTES",
                    GetConfigurationOrEnvironmentValue("OTEL_RESOURCE_ATTRIBUTES")
                },
                {
                    "PluginStateStore__Mode",
                    _configuration["PluginStateStore:Mode"]
                },
                {
                    "PluginStateStore__RootPath",
                    _configuration["PluginStateStore:RootPath"]
                },
                {
                    "PluginRuntime__Diagnostics",
                    _configuration["PluginRuntime:Diagnostics"]
                },
                {
                    "PluginRuntime__HydrationTimeoutSeconds",
                    _configuration["PluginRuntime:HydrationTimeoutSeconds"]
                },
                {
                    "PluginRuntime__HeartbeatIntervalSeconds",
                    _configuration["PluginRuntime:HeartbeatIntervalSeconds"]
                },
                {
                    "WorkflowBuilder__RepositoryRoot",
                    _configuration["WorkflowBuilder:RepositoryRoot"]
                    ?? Environment.GetEnvironmentVariable("WorkflowBuilder__RepositoryRoot")
                },
                {
                    "WORKFLOW_BUILDER_DEMO_REPOSITORY",
                    _configuration["WORKFLOW_BUILDER_DEMO_REPOSITORY"]
                    ?? Environment.GetEnvironmentVariable("WORKFLOW_BUILDER_DEMO_REPOSITORY")
                }
            };

            if (!string.IsNullOrWhiteSpace(blobConnectionString))
            {
                environmentVariables["AzureStorage__ConnectionString"] = blobConnectionString;
                environmentVariables[$"ConnectionStrings__{WorkflowBuilderTopology.Storage.BlobConnectionName}"] = blobConnectionString;
                environmentVariables[WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameEnvironmentVariable] = pluginsBlobContainerName;
                environmentVariables[WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerNameEnvironmentVariable] = artifactsBlobContainerName;
            }

            if (!string.IsNullOrWhiteSpace(cosmosConnectionString))
            {
                environmentVariables["AzureCosmos__ConnectionString"] = cosmosConnectionString;
                environmentVariables[WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable] = cosmosConnectionName;
                environmentVariables[WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable] =
                    _configuration[WorkflowBuilderTopology.Cosmos.DatabaseNameKey]
                    ?? WorkflowBuilderTopology.Cosmos.DefaultDatabaseName;
                environmentVariables[WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable] =
                    _configuration[WorkflowBuilderTopology.Cosmos.ResourceNameKey];
                environmentVariables[$"ConnectionStrings__{WorkflowBuilderTopology.Cosmos.DefaultDatabaseName}"] = cosmosConnectionString;
                environmentVariables[$"ConnectionStrings__{cosmosConnectionName}"] = cosmosConnectionString;

                _logger.LogInformation(
                    "Starting plugin runtime {PluginName} with Cosmos endpoint {CosmosEndpoint}.",
                    Name,
                    GetConnectionStringSegment(cosmosConnectionString, "AccountEndpoint") ?? "<unknown>");
            }

            var configFiles = await _pluginRuntimeConfigurationService.PrepareConfigurationFilesAsync(
                fileInfo,
                Name,
                cancellationToken);

            var cmd = CreateCommand(fileInfo, configFiles)
                .WithEnvironmentVariables(environmentVariables)
                .WithWorkingDirectory(fileInfo.Directory!.FullName)
                .WithValidation(CommandResultValidation.None)
                .WithStandardInputPipe(PipeSource.FromStream(stdinStream));

            await foreach (var cmdEvent in cmd.ListenAsync(cancellationToken))
            {
                switch (cmdEvent)
                {
                    case StartedCommandEvent started:
                        _process = Process.GetProcessById(started.ProcessId);
                        if (_operatingSystemInformation.IsWindows())
                        {
                            _childProcessTracker.Track(_process);
                        }
                        _logger.LogInformation("{PluginName} Process started; ID: {ProcessId}", Name, started.ProcessId);
                        break;
                    case StandardOutputCommandEvent stdOut:
                        await OnOutput(stdOut.Text, _processCancellation?.Token ?? cancellationToken);
                        break;
                    case StandardErrorCommandEvent stdErr:
                        _logger.LogError("{PluginName} Error: {ErrorText}", Name, stdErr.Text);
                        break;
                    case ExitedCommandEvent exited:
                        await FlushBufferedOutputAsync(
                            processRemainingText: true,
                            _processCancellation?.Token ?? cancellationToken);
                        var policyRequestedStop = Volatile.Read(ref _policyStopRequested) == 1;
                        var stopWasRequested =
                            !policyRequestedStop &&
                            (Volatile.Read(ref _stopRequested) == 1 ||
                             _processCancellation?.IsCancellationRequested == true);
                        var exitSummary = EnsureProtocolSession().RecordProcessExit(
                            exited.ExitCode,
                            stopWasRequested);

                        try
                        {
                            await _exitNotificationService.NotifyProcessExitAsync(
                                _identity,
                                exited.ExitCode,
                                stopWasRequested,
                                policyRequestedStop,
                                exitSummary,
                                cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "Failed to notify engine services that plugin runtime {PluginName} exited with {InFlightJobCount} in-flight process step(s).",
                                Name,
                                exitSummary.InFlightJobCount);
                        }

                        if (stopWasRequested)
                        {
                            _logger.LogInformation(
                                "{PluginName} Process exited during shutdown; Code: {ExitCode}",
                                Name,
                                exited.ExitCode);
                        }
                        else
                        {
                            _logger.LogCritical("{PluginName} Process exited; Code: {ExitCode}", Name, exited.ExitCode);
                        }

                        if (exitSummary.ExitedBeforeReady && !stopWasRequested)
                        {
                            _readySignal.TrySetException(
                                new InvalidOperationException($"{Name} exited before signalling readiness. Exit code: {exited.ExitCode}."));
                        }
                        else if (exitSummary.ExitedBeforeReady)
                        {
                            _readySignal.TrySetCanceled();
                        }

                        _processCancellation?.Cancel();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _readySignal.TrySetException(ex);
            throw;
        }
    }

    private Command CreateCommand(FileInfo fileInfo, IReadOnlyList<FileInfo> configFiles)
    {
        if (_operatingSystemInformation.IsLinux())
        {
            return Cli.Wrap("/bin/bash")
                .WithArguments(args =>
                {
                    args
                        .Add("-c")
                        .Add(LinuxProcessWrapperScript)
                        .Add("bash")
                        .Add(Environment.ProcessId.ToString())
                        .Add(fileInfo.FullName);

                    AddConfigArguments(args, configFiles);
                });
        }

        return Cli.Wrap(fileInfo.FullName)
            .WithArguments(args => AddConfigArguments(args, configFiles));
    }

    private static void AddConfigArguments(ArgumentsBuilder args, IEnumerable<FileInfo> configFiles)
    {
        foreach (var configFile in configFiles)
        {
            args
                .Add("--config")
                .Add(configFile.FullName);
        }
    }

    private async Task OnOutput(string output, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        _stdoutBuffer.Append(output);
        await FlushBufferedOutputAsync(processRemainingText: false, cancellationToken);
    }

    private async Task FlushBufferedOutputAsync(
        bool processRemainingText,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var bufferText = _stdoutBuffer.ToString();
            if (bufferText.Length == 0)
            {
                return;
            }

            var newlineIndex = bufferText.IndexOf('\n');
            if (newlineIndex >= 0)
            {
                var line = bufferText[..newlineIndex].TrimEnd('\r');
                _stdoutBuffer.Remove(0, newlineIndex + 1);
                await ProcessOutputLineAsync(line, cancellationToken);
                continue;
            }

            if (TryConsumeBufferedCompleteProtocolMessage(out var standaloneMessage))
            {
                await ProcessOutputLineAsync(standaloneMessage, cancellationToken);
                continue;
            }

            if (!processRemainingText)
            {
                return;
            }

            _stdoutBuffer.Clear();
            await ProcessOutputLineAsync(bufferText.TrimEnd('\r'), cancellationToken);
            return;
        }
    }

    private bool TryConsumeBufferedCompleteProtocolMessage(out string standaloneMessage)
    {
        standaloneMessage = string.Empty;

        var bufferText = _stdoutBuffer.ToString().Trim();
        if (bufferText.Length == 0)
        {
            _stdoutBuffer.Clear();
            return false;
        }

        if (!bufferText.StartsWith('{') || !bufferText.EndsWith('}'))
        {
            return false;
        }

        _stdoutBuffer.Clear();
        standaloneMessage = bufferText;
        return true;
    }

    private async Task ProcessOutputLineAsync(
        string outputLine,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputLine))
        {
            return;
        }

        var disposition = await EnsureProtocolSession().ProcessOutputLineAsync(outputLine, cancellationToken);
        if (disposition is PluginRuntimeProtocolLineDisposition.Ready)
        {
            _readySignal.TrySetResult();
        }
        else if (disposition is PluginRuntimeProtocolLineDisposition.Error &&
                 !EnsureProtocolSession().IsReady)
        {
            _readySignal.TrySetException(
                new InvalidOperationException($"{Name} emitted a protocol error before signalling readiness."));
        }
    }

    public async Task<bool> QueueJob(
        InvocationEnvelope invocationEnvelope,
        CancellationToken cancellationToken)
    {
        await _queueWriterGate.WaitAsync(cancellationToken);
        try
        {
            var payload = InvocationEnvelopeSerializer.Serialize(invocationEnvelope);
            var writer = _commsStream.Writer;
            var buffer = writer.GetMemory(payload.Length + 1);
            payload.Span.CopyTo(buffer.Span);
            buffer.Span[payload.Length] = (byte)'\n';
            writer.Advance(payload.Length + 1);
            await writer.FlushAsync(cancellationToken);
            EnsureProtocolSession().TrackQueuedJob(invocationEnvelope.ProcessStepId);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _queueWriterGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopCoreAsync(policyRequested: false, cancellationToken);
    }

    public async Task StopForHealthAsync(CancellationToken cancellationToken)
    {
        await StopCoreAsync(policyRequested: true, cancellationToken);
    }

    private async Task StopCoreAsync(bool policyRequested, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        if (policyRequested)
        {
            Interlocked.Exchange(ref _policyStopRequested, 1);
        }

        if (_processCancellation is not null)
        {
            _processCancellation.Cancel();
            _processCancellation.Dispose();
            _processCancellation = null;
        }

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                // Swallow exceptions from querying an exited or disposed process.
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
    }

    private void StartHeartbeatMonitor()
    {
        var timeout = _healthOptions.EffectiveHeartbeatTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        var cancellationToken = _processCancellation?.Token ?? CancellationToken.None;
        _heartbeatMonitorTask = Task.Run(
            () => MonitorHeartbeatAsync(timeout, cancellationToken),
            CancellationToken.None);
    }

    private async Task MonitorHeartbeatAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var pollInterval = TimeSpan.FromMilliseconds(
            Math.Clamp(timeout.TotalMilliseconds / 2, 500, 5000));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _asyncDelay.DelayAsync(pollInterval, cancellationToken);
                var session = EnsureProtocolSession();
                if (!session.IsReady)
                {
                    continue;
                }

                var elapsed = DateTimeOffset.UtcNow - session.LastHeartbeatAt;
                if (elapsed < timeout)
                {
                    continue;
                }

                if (Interlocked.Exchange(ref _heartbeatMissedPublished, 1) == 0)
                {
                    await PublishHealthEventAsync(
                        PluginAgentHealthEventType.HeartbeatMissed,
                        message: $"Plugin agent heartbeat was not observed for {elapsed.TotalSeconds:0} seconds.",
                        cancellationToken: cancellationToken);
                }

                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during process shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Plugin runtime {PluginName} heartbeat monitor failed.",
                Name);
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var startupTimeout = TimeSpan.FromSeconds(
            Math.Max(1, _configuration.GetValue("PluginRuntime:StartupTimeoutSeconds", 30)));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(startupTimeout);
        using var registration = timeoutCts.Token.Register(
            () => _readySignal.TrySetCanceled(timeoutCts.Token));

        try
        {
            await _readySignal.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timeoutException = new TimeoutException(
                $"{Name} did not signal readiness within {startupTimeout.TotalSeconds:0} seconds.");
            await PublishHealthEventAsync(
                PluginAgentHealthEventType.StartupTimeout,
                exception: timeoutException,
                cancellationToken: CancellationToken.None);
            throw timeoutException;
        }
    }

    private static string? GetConnectionStringSegment(string connectionString, string segmentName)
    {
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            if (segment[..separatorIndex].Equals(segmentName, StringComparison.OrdinalIgnoreCase))
            {
                return segment[(separatorIndex + 1)..];
            }
        }

        return null;
    }

    private string? GetConfigurationOrEnvironmentValue(string name) =>
        _configuration[name] ?? Environment.GetEnvironmentVariable(name);

    private PluginRuntimeProtocolSession EnsureProtocolSession()
    {
        return _protocolSession ??= new PluginRuntimeProtocolSession(
            _identity,
            _resultFinalizationQueue,
            _agentTelemetry,
            _healthEventBus,
            _logger);
    }

    private async Task PublishHealthEventAsync(
        PluginAgentHealthEventType eventType,
        Exception? exception = null,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _healthEventBus.PublishAsync(
                PluginAgentHealthEvent.Create(
                    _identity,
                    eventType,
                    exception: exception,
                    message: message),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish plugin-agent health event {EventType} for {PluginName}.",
                eventType,
                Name);
        }
    }
}

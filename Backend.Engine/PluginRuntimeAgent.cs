using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using Backend.Engine.Debugging;
using Backend.Plugins.Communication;
using CliWrap;
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
    private readonly IChildProcessTracker _childProcessTracker;
    private readonly IOperatingSystemInformation _operatingSystemInformation;
    private Process? _process;
    private CancellationTokenSource? _processCancellation;
    private Pipe _commsStream = new();
    private bool ready = false;
    private readonly TaskCompletionSource _readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _queueWriterGate = new(1, 1);
    private Task? _runTask;
    private readonly StringBuilder _stdoutBuffer = new();
    private int _stopRequested;

    private string _name = string.Empty;
    public string Name => _name;

    public PluginRuntimeAgent(
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        ILogger<PluginRuntimeAgent> logger,
        PluginResultFinalizationQueue resultFinalizationQueue,
        IChildProcessTracker childProcessTracker,
        IOperatingSystemInformation operatingSystemInformation)
    {
        _configuration = configuration;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
        _resultFinalizationQueue = resultFinalizationQueue;
        _childProcessTracker = childProcessTracker;
        _operatingSystemInformation = operatingSystemInformation;
    }

    public async Task InitialiseAsync(
        FileInfo entryPoint,
        string name,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        _name = name;
        _stopRequested = 0;

        if (_process is not null)
        {
            throw new InvalidOperationException("Plugin runtime process is already initialised.");
        }

        _runTask = Run(entryPoint, token);

        try
        {
            await WaitUntilReadyAsync(token);
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
                _configuration["AzureCosmos:ConnectionName"]
                ?? "enginedb";
            var cosmosConnectionString =
                LocalDevelopmentResourceSettings.GetCosmosConnectionString(
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
                }
            };

            if (!string.IsNullOrWhiteSpace(cosmosConnectionString))
            {
                environmentVariables["AzureCosmos__ConnectionString"] = cosmosConnectionString;
                environmentVariables["AzureCosmos__ConnectionName"] = cosmosConnectionName;
                environmentVariables["AzureCosmos__DatabaseName"] =
                    _configuration["AzureCosmos:DatabaseName"] ?? "enginedb";
                environmentVariables["AzureCosmos__ResourceName"] =
                    _configuration["AzureCosmos:ResourceName"];
                environmentVariables["ConnectionStrings__enginedb"] = cosmosConnectionString;
                environmentVariables[$"ConnectionStrings__{cosmosConnectionName}"] = cosmosConnectionString;

                _logger.LogInformation(
                    "Starting plugin runtime {PluginName} with Cosmos endpoint {CosmosEndpoint}.",
                    Name,
                    GetConnectionStringSegment(cosmosConnectionString, "AccountEndpoint") ?? "<unknown>");
            }

            var cmd = CreateCommand(fileInfo)
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
                        await OnOutput(stdOut.Text);
                        break;
                    case StandardErrorCommandEvent stdErr:
                        _logger.LogError("{PluginName} Error: {ErrorText}", Name, stdErr.Text);
                        break;
                    case ExitedCommandEvent exited:
                        await FlushBufferedOutputAsync(processRemainingText: true);
                        var stopWasRequested = Volatile.Read(ref _stopRequested) == 1 ||
                                               _processCancellation?.IsCancellationRequested == true;

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

                        if (!ready && !stopWasRequested)
                        {
                            _readySignal.TrySetException(
                                new InvalidOperationException($"{Name} exited before signalling readiness. Exit code: {exited.ExitCode}."));
                        }
                        else if (!ready)
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

    private Command CreateCommand(FileInfo fileInfo)
    {
        if (_operatingSystemInformation.IsLinux())
        {
            return Cli.Wrap("/bin/bash")
                .WithArguments(args => args
                    .Add("-c")
                    .Add(LinuxProcessWrapperScript)
                    .Add("bash")
                    .Add(Environment.ProcessId.ToString())
                    .Add(fileInfo.FullName));
        }

        return Cli.Wrap(fileInfo.FullName);
    }

    private async Task OnOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        _stdoutBuffer.Append(output);
        await FlushBufferedOutputAsync(processRemainingText: false);
    }

    private async Task FlushBufferedOutputAsync(bool processRemainingText)
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
                await ProcessOutputLineAsync(line);
                continue;
            }

            if (TryConsumeBufferedStandaloneMessage(out var standaloneMessage))
            {
                await ProcessOutputLineAsync(standaloneMessage);
                continue;
            }

            if (!processRemainingText)
            {
                return;
            }

            _stdoutBuffer.Clear();
            await ProcessOutputLineAsync(bufferText.TrimEnd('\r'));
            return;
        }
    }

    private bool TryConsumeBufferedStandaloneMessage(out string standaloneMessage)
    {
        standaloneMessage = string.Empty;

        var bufferText = _stdoutBuffer.ToString().Trim();
        if (bufferText.Length == 0)
        {
            _stdoutBuffer.Clear();
            return false;
        }

        if (!ready)
        {
            if (!string.Equals(bufferText, "Ready", StringComparison.Ordinal))
            {
                return false;
            }

            _stdoutBuffer.Clear();
            standaloneMessage = "Ready";
            return true;
        }

        try
        {
            PluginExecutionResultSerializer.Deserialize(Encoding.UTF8.GetBytes(bufferText));
            _stdoutBuffer.Clear();
            standaloneMessage = bufferText;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ProcessOutputLineAsync(string outputLine)
    {
        if (string.IsNullOrWhiteSpace(outputLine))
        {
            return;
        }

        if (!ready)
        {
            if (outputLine == "Ready")
            {
                ready = true;
                _readySignal.TrySetResult();
                return;
            }

            _logger.LogDebug(
                "Ignoring pre-ready stdout from plugin runtime {PluginName}: {Output}",
                Name,
                outputLine);
            return;
        }

        try
        {
            var result = PluginExecutionResultSerializer.Deserialize(Encoding.UTF8.GetBytes(outputLine));
            await _resultFinalizationQueue.EnqueueAsync(result, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to parse output from plugin runtime {PluginName}: {Output}",
                Name,
                outputLine);
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
        Interlocked.Exchange(ref _stopRequested, 1);

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
            throw new TimeoutException(
                $"{Name} did not signal readiness within {startupTimeout.TotalSeconds:0} seconds.");
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
}

using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using Backend.Engine.Debugging;
using CliWrap;
using CliWrap.EventStream;

namespace Backend.Engine;

public class NanoServiceAgent
{
    private const string LinuxProcessWrapperScript = "parent_pid=\"$1\"; shift; \"$@\" & child_pid=$!; trap 'kill -TERM \"$child_pid\" 2>/dev/null; wait \"$child_pid\" 2>/dev/null' EXIT INT TERM; while kill -0 \"$parent_pid\" 2>/dev/null && kill -0 \"$child_pid\" 2>/dev/null; do sleep 1; done; kill -TERM \"$child_pid\" 2>/dev/null; wait \"$child_pid\"";

    private readonly IConfiguration _configuration;
    private readonly EngineInstanceInfo _engineInstanceInfo;
    private readonly ILogger _logger;
    private readonly JobManager _jobManager;
    private readonly VisualStudioDebuggerAttacher _debuggerAttacher;
    private readonly IChildProcessTracker _childProcessTracker;
    private readonly IOperatingSystemInformation _operatingSystemInformation;
    private Process? _process;
    private CancellationTokenSource? _processCancellation;
    private Pipe _commsStream = new();
    private bool ready = false;

    private string _name;
    public string Name => _name;

    public NanoServiceAgent(
        IConfiguration configuration,
        EngineInstanceInfo engineInstanceInfo,
        ILogger<NanoServiceAgent> logger,
        JobManager jobManager,
        VisualStudioDebuggerAttacher debuggerAttacher,
        IChildProcessTracker childProcessTracker,
        IOperatingSystemInformation operatingSystemInformation)
    {
        _configuration = configuration;
        _engineInstanceInfo = engineInstanceInfo;
        _logger = logger;
        _jobManager = jobManager;
        _debuggerAttacher = debuggerAttacher;
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

        if (_process is not null)
        {
            throw new InvalidOperationException("Nano-service process already initialised.");
        }

        await Run(entryPoint, token);
    }

    public async Task Run(
        FileInfo fileInfo,
        CancellationToken cancellationToken)
    {
        _processCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await using var stdinStream = _commsStream.Reader.AsStream(leaveOpen: true);

        var cmd = CreateCommand(fileInfo)
            .WithEnvironmentVariables(
                new Dictionary<string, string?>
                {
                    {
                        "OTEL_EXPORTER_OTLP_ENDPOINT",
                        _configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
                    },
                    {
                        "OTEL_EXPORTER_OTLP_PROTOCOL",
                        _configuration["OTEL_EXPORTER_OTLP_PROTOCOL"]
                    },
                    {
                        "OTEL_EXPORTER_OTLP_HEADERS",
                        _configuration["OTEL_EXPORTER_OTLP_HEADERS"]
                    }
                }
            )
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
                    _logger.LogCritical("{PluginName} Process exited; Code: {ExitCode}", Name, exited.ExitCode);
                    _processCancellation.Cancel();
                    break;
            }
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
        if (!ready)
        {
            if (output == "Ready") ready = true;
            return;
        }

        var jobCompletionEvent = JsonSerializer.Deserialize<JobCompletionEvent>(output);
        await _jobManager.CompleteJob(jobCompletionEvent!, CancellationToken.None);
    }

    public async Task<bool> QueueJob(
        JobInitialisationEvent jobInitialisationEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await JsonSerializer.SerializeAsync(
                _commsStream.Writer,
                jobInitialisationEvent,
                JsonSerializerOptions.Default,
                cancellationToken);

            await _commsStream.Writer.FlushAsync(cancellationToken);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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
}

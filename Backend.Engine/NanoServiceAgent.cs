using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using Backend.Engine.Debugging;
using CliWrap;
using CliWrap.EventStream;

namespace Backend.Engine;

public class NanoServiceAgent
{
    private readonly EngineInstanceInfo _engineInstanceInfo;
    private readonly ILogger _logger;
    private readonly JobManager _jobManager;
    private readonly VisualStudioDebuggerAttacher _debuggerAttacher;
    private Process? _process;
    private CommandTask<CommandResult>? _processTask;
    private CancellationTokenSource? _processCancellation;
    private Pipe _commsStream = new();

    public NanoServiceAgent(
        EngineInstanceInfo engineInstanceInfo,
        ILogger<NanoServiceAgent> logger,
        JobManager jobManager,
        VisualStudioDebuggerAttacher debuggerAttacher)
    {
        _engineInstanceInfo = engineInstanceInfo;
        _logger = logger;
        _jobManager = jobManager;
        _debuggerAttacher = debuggerAttacher;
    }

    public Task InitialiseAsync(
        FileInfo entryPoint,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);

        if (_processTask is not null)
        {
            throw new InvalidOperationException("Nano-service process already initialised.");
        }

        _processTask = Run(entryPoint, token);

        return Task.CompletedTask;
    }

    public async Task Run(
        FileInfo fileInfo,
        CancellationToken cancellationToken)
    {
        _processCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await using var stdinStream = _commsStream.Reader.AsStream();

        var cmd = Cli.Wrap(fileInfo.FullName)
            .WithWorkingDirectory(fileInfo.Directory!.FullName)
            .WithValidation(CommandResultValidation.None)
            .WithStandardInputPipe(PipeSource.FromStream(stdinStream));

        await foreach (var cmdEvent in cmd.ListenAsync(cancellationToken))
        {
            switch (cmdEvent)
            {
                case StartedCommandEvent started:
                    _process = Process.GetProcessById(started.ProcessId);
                    _logger.LogInformation("Process started; ID: {ProcessId}", started.ProcessId);
                    break;
                case StandardOutputCommandEvent stdOut:
                    await OnOutput(stdOut.Text);
                    break;
                case StandardErrorCommandEvent stdErr:
                    _logger.LogError("Error: {ErrorText}", stdErr.Text);
                    break;
                case ExitedCommandEvent exited:
                    _logger.LogCritical("Process exited; Code: {ExitCode}", exited.ExitCode);
                    break;
            }
        }
    }

    private async Task OnOutput(string output)
    {
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

        if (_processTask is not null)
        {
            try
            {
                await _processTask;
            }
            catch (OperationCanceledException)
            {
                // Ignored - process was cancelled intentionally.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while waiting for nano service process to exit.");
            }

            _processTask = null;
        }

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.CloseMainWindow();
                    if (!_process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds))
                    {
                        _process.Kill(true);
                    }
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

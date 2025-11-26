using System.Diagnostics;
using System.IO.Pipelines;
using Backend.Engine.Debugging;
using Backend.Plugins.Communication;
using CliWrap;

namespace Backend.Engine;

public class NanoServiceAgent
{
    private readonly EngineInstanceInfo _engineInstanceInfo;
    private readonly ILogger _logger;
    private readonly NanoServiceSignalsClient _signalsClient;
    private readonly TaskFinalizationService _taskFinalizationService;
    private readonly VisualStudioDebuggerAttacher _debuggerAttacher;
    private readonly Queue<TaskContext> _tasks = new();
    private readonly SemaphoreSlim _taskProcessingLock = new(1, 1);
    private Process? _process;
    private CommandTask<CommandResult>? _processTask;
    private CancellationTokenSource? _processCancellation;
    private Pipe? _stdinPipe;
    private Pipe? _stdoutPipe;
    private Stream? _channelOutboundStream;
    private Stream? _channelInboundStream;

    public NanoServiceAgent(
        EngineInstanceInfo engineInstanceInfo,
        ILogger<NanoServiceAgent> logger,
        NanoServiceSignalsClient signalsClient,
        TaskFinalizationService taskFinalizationService,
        VisualStudioDebuggerAttacher debuggerAttacher)
    {
        _engineInstanceInfo = engineInstanceInfo;
        _logger = logger;
        _signalsClient = signalsClient;
        _taskFinalizationService = taskFinalizationService;
        _debuggerAttacher = debuggerAttacher;
    }

    public async Task<bool> InitialiseAsync(
        FileInfo fileInfo,
        IEnumerable<NanoServiceInputChannelModel> inputChannels,
        NanoServiceGlobalStateSnapshot? globalState,
        CancellationToken cancellationToken)
    {
        _processCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (!StartNanoServiceProcess(fileInfo))
        {
            _processCancellation.Dispose();
            _processCancellation = null;
            return false;
        }

        await _debuggerAttacher.TryAttachAsync(_process, cancellationToken);

        await _signalsClient.EstablishAsync(
            _process,
            fileInfo.Name,
            _channelOutboundStream!,
            _channelInboundStream!,
            inputChannels,
            globalState,
            cancellationToken);

        return true;
    }

    private static Command BuildCommand(FileInfo executableFileInfo)
        => Cli.Wrap(executableFileInfo.FullName)
            .WithWorkingDirectory(executableFileInfo.Directory!.FullName)
            .WithValidation(CommandResultValidation.None);

    private bool StartNanoServiceProcess(FileInfo executableFileInfo)
    {
        _stdinPipe = new Pipe();
        _stdoutPipe = new Pipe();

        _channelOutboundStream = _stdinPipe.Writer.AsStream(leaveOpen: true);
        _channelInboundStream = _stdoutPipe.Reader.AsStream(leaveOpen: true);

        _processTask = BuildCommand(executableFileInfo)
            .ExecuteAsync(
                stdIn: PipeSource.FromPipeReader(_stdinPipe.Reader),
                stdOut: PipeTarget.ToPipeWriter(_stdoutPipe.Writer),
                stdErr: PipeTarget.ToDelegate(
                    line => _logger.LogInformation("[{NanoServiceName}] {Line}", executableFileInfo.Name, line.TrimEnd())),
                cancellationToken: _processCancellation!.Token);

        if (_processTask.ProcessId <= 0)
        {
            return false;
        }

        _process = Process.GetProcessById(_processTask.ProcessId);
        return true;
    }

    public bool EnqueueTask(TaskContext taskContext)
    {
        _tasks.Enqueue(taskContext);
        _ = ProcessTasksAsync(CancellationToken.None);
        return true;
    }

    private async Task ProcessTasksAsync(CancellationToken cancellationToken)
    {
        if (!await _taskProcessingLock.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return;
        }

        try
        {
            while (_tasks.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                var taskContext = _tasks.Dequeue();
                await ProcessTaskAsync(taskContext, cancellationToken);
            }
        }
        finally
        {
            _taskProcessingLock.Release();
        }
    }

    private async Task ProcessTaskAsync(TaskContext taskContext, CancellationToken cancellationToken)
    {
        try
        {
            await taskContext.ExtendProcessingWindow();
            var envelope = new InvocationEnvelope(
                taskContext.WorkflowInstanceId,
                taskContext.WorkflowTaskInstanceId,
                taskContext.InputChannelName);

            _logger.LogInformation(
                "Sending invocation envelope for workflow instance {WorkflowInstanceId} task {TaskInstanceId}",
                taskContext.WorkflowInstanceId,
                taskContext.WorkflowTaskInstanceId);

            await _signalsClient.SendInvocationAsync(envelope, cancellationToken);

            _logger.LogInformation(
                "Invocation envelope dispatched for workflow instance {WorkflowInstanceId} task {TaskInstanceId}",
                taskContext.WorkflowInstanceId,
                taskContext.WorkflowTaskInstanceId);

            await taskContext.ExtendProcessingWindow();

            var outputs = await _signalsClient.ReceiveOutputsAsync(cancellationToken);

            _logger.LogInformation(
                "Received {OutputCount} outputs for workflow instance {WorkflowInstanceId} task {TaskInstanceId}",
                outputs.Count,
                taskContext.WorkflowInstanceId,
                taskContext.WorkflowTaskInstanceId);

            await _taskFinalizationService.FinalizeSuccessAsync(
                taskContext,
                outputs,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error while processing workflow instance {WorkflowInstanceId} task {TaskInstanceId}",
                taskContext.WorkflowInstanceId,
                taskContext.WorkflowTaskInstanceId);

            try
            {
                await _taskFinalizationService.FinalizeFailureAsync(taskContext, cancellationToken);
            }
            catch (Exception finalizeEx)
            {
                _logger.LogError(
                    finalizeEx,
                    "Failed to finalize workflow instance {WorkflowInstanceId} task {TaskInstanceId} after error",
                    taskContext.WorkflowInstanceId,
                    taskContext.WorkflowTaskInstanceId);
            }
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

        await _signalsClient.DisposeAsync();

        if (_channelOutboundStream is not null)
        {
            await _channelOutboundStream.DisposeAsync();
            _channelOutboundStream = null;
        }

        if (_channelInboundStream is not null)
        {
            await _channelInboundStream.DisposeAsync();
            _channelInboundStream = null;
        }

        _stdinPipe?.Writer.Complete();
        _stdinPipe?.Reader.Complete();
        _stdoutPipe?.Writer.Complete();
        _stdoutPipe?.Reader.Complete();
        _stdinPipe = null;
        _stdoutPipe = null;
    }
}

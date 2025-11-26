using System.Diagnostics;
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
    private string _engineOutputPipe;
    private string _nanoServiceInputPipe;

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
        _engineOutputPipe = $"engine-{_engineInstanceInfo.InstanceId}-to-{fileInfo.Name}-pipe";
        _nanoServiceInputPipe = $"{fileInfo.Name}-to-engine-{_engineInstanceInfo.InstanceId}-pipe";

        var sessionId = Guid.NewGuid();
        var channelOptions = new NamedPipeChannelOptions(
            sessionId,
            _engineOutputPipe,
            _nanoServiceInputPipe,
            PipeConnectionRole.EngineServer);

        _processCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _processTask = BuildCommand(fileInfo, channelOptions).ExecuteAsync(_processCancellation.Token);

        if (_processTask.ProcessId <= 0)
        {
            return false;
        }

        _process = Process.GetProcessById(_processTask.ProcessId);

        await _debuggerAttacher.TryAttachAsync(_process, cancellationToken);

        await _signalsClient.EstablishAsync(
            _process,
            fileInfo.Name,
            inputChannels,
            globalState,
            channelOptions,
            cancellationToken);

        return true;
    }

    private Command BuildCommand(FileInfo executableFileInfo, NamedPipeChannelOptions channelOptions)
        => Cli.Wrap(executableFileInfo.FullName)
            .WithArguments(args => args
                .Add($"--Signals:OutboundPipe={channelOptions.OutboundPipeName}")
                .Add($"--Signals:InboundPipe={channelOptions.InboundPipeName}")
                .Add($"--Signals:SessionId={channelOptions.SessionId}")
                .Add($"--Signals:Role={channelOptions.Role}"))
            .WithWorkingDirectory(executableFileInfo.Directory!.FullName)
            .WithEnvironmentVariables(env =>
            {
                env.Set("Signals__OutboundPipe", channelOptions.OutboundPipeName);
                env.Set("Signals__InboundPipe", channelOptions.InboundPipeName);
                env.Set("Signals__SessionId", channelOptions.SessionId.ToString());
                env.Set("Signals__Role", channelOptions.Role.ToString());
            })
            .WithStandardOutputPipe(PipeTarget.ToDelegate(
                line => _logger.LogInformation("[{NanoServiceName}] {Line}", executableFileInfo.Name, line.TrimEnd())))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(
                line => _logger.LogInformation("[{NanoServiceName}] {Line}", executableFileInfo.Name, line.TrimEnd())))
            .WithValidation(CommandResultValidation.None);

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
    }
}

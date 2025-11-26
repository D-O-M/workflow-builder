using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Backend.Engine.Debugging;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public class NanoServiceAgent
{
    private readonly ILogger _logger;
    private readonly NanoServiceSignalsClient _signalsClient;
    private readonly TaskFinalizationService _taskFinalizationService;
    private readonly VisualStudioDebuggerAttacher _debuggerAttacher;
    private readonly Queue<TaskContext> _tasks = new();
    private readonly SemaphoreSlim _taskProcessingLock = new(1, 1);
    private Process? _process;

    public NanoServiceAgent(
        ILogger<NanoServiceAgent> logger,
        NanoServiceSignalsClient signalsClient,
        TaskFinalizationService taskFinalizationService,
        VisualStudioDebuggerAttacher debuggerAttacher)
    {
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
        var sessionId = Guid.NewGuid();
        var channelOptions = new NamedPipeChannelOptions(
            sessionId,
            $"signals-{sessionId:N}-out",
            $"signals-{sessionId:N}-in",
            PipeConnectionRole.EngineServer);

        _process = CreateProcess(fileInfo, channelOptions);
        if (!_process.Start())
        {
            return false;
        }

        await _debuggerAttacher.TryAttachAsync(_process, cancellationToken);

        await _signalsClient.EstablishAsync(
            _process,
            fileInfo.Name,
            inputChannels,
            globalState,
            channelOptions,
            cancellationToken);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return true;
    }

    private Process CreateProcess(FileInfo executableFileInfo, NamedPipeChannelOptions channelOptions)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executableFileInfo.FullName,
            Arguments =
                $"--Signals:OutboundPipe={channelOptions.OutboundPipeName} " +
                $"--Signals:InboundPipe={channelOptions.InboundPipeName} " +
                $"--Signals:SessionId={channelOptions.SessionId} " +
                $"--Signals:Role={channelOptions.Role}",
            WorkingDirectory = executableFileInfo.Directory!.FullName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        psi.Environment["Signals__OutboundPipe"] = channelOptions.OutboundPipeName;
        psi.Environment["Signals__InboundPipe"] = channelOptions.InboundPipeName;
        psi.Environment["Signals__SessionId"] = channelOptions.SessionId.ToString();
        psi.Environment["Signals__Role"] = channelOptions.Role.ToString();

        var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogInformation(
                    "[{NanoServiceName}] {Line}",
                    executableFileInfo.Name,
                    e.Data);
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogInformation(
                    "[{NanoServiceName}] {Line}",
                    executableFileInfo.Name,
                    e.Data);
            }
        };

        return process;
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

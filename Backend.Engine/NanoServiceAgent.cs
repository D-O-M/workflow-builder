using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backend.Engine.Debugging;
using Backend.Engine.ProtocolHandlers;
using Backend.Plugins.Communication;
using Backend.Plugins.Communication.Protocols.Invocation;
using Backend.Plugins.Communication.Protocols.JobCompletion;

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
        _process = CreateProcess(fileInfo);
        if (!_process.Start())
        {
            return false;
        }

        await _debuggerAttacher.TryAttachAsync(_process, cancellationToken);

        await _signalsClient.EstablishAsync(_process, fileInfo.Name, inputChannels, globalState, cancellationToken);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return true;
    }

    private Process CreateProcess(FileInfo executableFileInfo)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executableFileInfo.FullName,
            Arguments = "",
            WorkingDirectory = executableFileInfo.Directory!.FullName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

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
            var channel = _signalsClient.Channel ?? throw new InvalidOperationException("Nano service channel is not ready.");

            var inputBytes = await ReadAllBytesAsync(await taskContext.InputChannelDataStream, cancellationToken);
            var globalStateStream = await taskContext.GetGlobalStateDataStreamAsync();
            var globalStateBytes = globalStateStream is null
                ? Array.Empty<byte>()
                : await ReadAllBytesAsync(globalStateStream, cancellationToken);

            var globalStateAction = new SignalAction(
                SignalActionClass.GlobalState,
                "global-state",
                null,
                "application/octet-stream",
                globalStateBytes);

            var invocationInputAction = new SignalAction(
                SignalActionClass.InputChannel,
                taskContext.InputChannelName,
                null,
                "application/octet-stream",
                inputBytes);

            var invocationInitiator = new InvocationInitiator(
                channel,
                globalStateAction,
                invocationInputAction,
                cancellationToken);

            _logger.LogInformation(
                "Starting invocation protocol for workflow instance {WorkflowInstanceId} task {TaskInstanceId}",
                taskContext.WorkflowInstanceId,
                taskContext.WorkflowTaskInstanceId);

            var invocationSteps = new InvocationInitiatorSteps(invocationInitiator);
            await invocationSteps.Perform();

            _logger.LogInformation(
                "Invocation protocol completed for workflow instance {WorkflowInstanceId} task {TaskInstanceId}",
                taskContext.WorkflowInstanceId,
                taskContext.WorkflowTaskInstanceId);

            await taskContext.ExtendProcessingWindow();

            var jobCompletionAcceptor = new JobCompletionAcceptor(channel, cancellationToken);
            var jobCompletionSteps = new JobCompletionAcceptorSteps(jobCompletionAcceptor);

            await jobCompletionSteps.Perform();

            _logger.LogInformation(
                "Received {OutputCount} outputs for workflow instance {WorkflowInstanceId} task {TaskInstanceId}",
                jobCompletionAcceptor.ReceivedOutputs.Count,
                taskContext.WorkflowInstanceId,
                taskContext.WorkflowTaskInstanceId);

            await _taskFinalizationService.FinalizeSuccessAsync(
                taskContext,
                jobCompletionAcceptor.ReceivedOutputs,
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

    private static async Task<byte[]> ReadAllBytesAsync(Stream source, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        await source.CopyToAsync(memoryStream, cancellationToken);
        return memoryStream.ToArray();
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

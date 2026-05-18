using System.Threading.Channels;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public enum PluginRuntimeProtocolLineDisposition
{
    Ignored = 0,
    Ready = 1,
    ResultQueued = 2,
    UnknownJobResult = 3,
    Log = 4,
    Error = 5,
    Heartbeat = 6,
    Malformed = 7,
    QueueClosed = 8
}

public sealed record PluginRuntimeProcessExitSummary(
    bool ExitedBeforeReady,
    IReadOnlyList<Guid> InFlightJobIds)
{
    public int InFlightJobCount => InFlightJobIds.Count;
}

public sealed class PluginRuntimeProtocolSession
{
    private readonly string _pluginName;
    private readonly PluginResultFinalizationQueue _resultFinalizationQueue;
    private readonly PluginRuntimeAgentTelemetry _telemetry;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly HashSet<Guid> _inFlightJobs = [];

    public PluginRuntimeProtocolSession(
        string pluginName,
        PluginResultFinalizationQueue resultFinalizationQueue,
        PluginRuntimeAgentTelemetry telemetry,
        ILogger logger)
    {
        _pluginName = pluginName;
        _resultFinalizationQueue = resultFinalizationQueue;
        _telemetry = telemetry;
        _logger = logger;
    }

    public bool IsReady { get; private set; }

    public int InFlightJobCount
    {
        get
        {
            lock (_sync)
            {
                return _inFlightJobs.Count;
            }
        }
    }

    public void TrackQueuedJob(Guid jobId)
    {
        lock (_sync)
        {
            _inFlightJobs.Add(jobId);
        }
    }

    public async Task<PluginRuntimeProtocolLineDisposition> ProcessOutputLineAsync(
        string outputLine,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputLine))
        {
            return PluginRuntimeProtocolLineDisposition.Ignored;
        }

        PluginProcessMessage message;
        try
        {
            message = PluginProcessMessageSerializer.DeserializeLine(outputLine);
        }
        catch (Exception ex)
        {
            _telemetry.RecordProtocolError(_pluginName, ex);
            _logger.LogWarning(
                ex,
                "Malformed plugin process protocol frame from {PluginName}: {Output}",
                _pluginName,
                outputLine);
            return PluginRuntimeProtocolLineDisposition.Malformed;
        }

        try
        {
            return message.Kind switch
            {
                PluginProcessMessageKind.Ready => HandleReady(),
                PluginProcessMessageKind.Result => await HandleResultAsync(message, cancellationToken),
                PluginProcessMessageKind.Log => HandleLog(message),
                PluginProcessMessageKind.Error => HandleError(message),
                PluginProcessMessageKind.Heartbeat => HandleHeartbeat(message),
                _ => PluginRuntimeProtocolLineDisposition.Ignored
            };
        }
        catch (PluginProcessProtocolException ex)
        {
            _telemetry.RecordProtocolError(_pluginName, ex);
            _logger.LogWarning(
                ex,
                "Invalid plugin process protocol frame from {PluginName}.",
                _pluginName);
            return PluginRuntimeProtocolLineDisposition.Malformed;
        }
    }

    public PluginRuntimeProcessExitSummary RecordProcessExit(
        int exitCode,
        bool shutdownRequested = false)
    {
        Guid[] inFlightJobIds;
        lock (_sync)
        {
            inFlightJobIds = [.. _inFlightJobs];
            _inFlightJobs.Clear();
        }

        if (inFlightJobIds.Length > 0 && !shutdownRequested)
        {
            _telemetry.RecordInFlightJobsOnExit(_pluginName, inFlightJobIds.Length);
            _logger.LogWarning(
                "Plugin runtime {PluginName} exited with {InFlightJobCount} in-flight process step(s). Exit code: {ExitCode}. Service Bus redelivery will recover unfinished work.",
                _pluginName,
                inFlightJobIds.Length,
                exitCode);
        }
        else if (inFlightJobIds.Length > 0)
        {
            _logger.LogInformation(
                "Plugin runtime {PluginName} exited during shutdown with {InFlightJobCount} in-flight process step(s). Exit code: {ExitCode}.",
                _pluginName,
                inFlightJobIds.Length,
                exitCode);
        }

        return new PluginRuntimeProcessExitSummary(!IsReady, inFlightJobIds);
    }

    private PluginRuntimeProtocolLineDisposition HandleReady()
    {
        IsReady = true;
        return PluginRuntimeProtocolLineDisposition.Ready;
    }

    private async Task<PluginRuntimeProtocolLineDisposition> HandleResultAsync(
        PluginProcessMessage message,
        CancellationToken cancellationToken)
    {
        PluginExecutionResult result;
        try
        {
            result = PluginProcessMessageSerializer.ReadResultPayload(message);
        }
        catch (Exception ex)
        {
            _telemetry.RecordProtocolError(_pluginName, ex);
            _logger.LogWarning(
                ex,
                "Invalid plugin result protocol frame from {PluginName}.",
                _pluginName);
            return PluginRuntimeProtocolLineDisposition.Malformed;
        }

        lock (_sync)
        {
            if (!_inFlightJobs.Remove(result.JobId))
            {
                _telemetry.RecordUnknownJobResult(_pluginName);
                _logger.LogWarning(
                    "Received a plugin result for unknown or inactive process step {ProcessStepId} from {PluginName}.",
                    result.JobId,
                    _pluginName);
                return PluginRuntimeProtocolLineDisposition.UnknownJobResult;
            }
        }

        try
        {
            await _resultFinalizationQueue.EnqueueAsync(result, cancellationToken);
            return PluginRuntimeProtocolLineDisposition.ResultQueued;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChannelClosedException ex)
        {
            _logger.LogWarning(
                ex,
                "Plugin result finalization queue is closed for plugin runtime {PluginName}; process-step redelivery will recover unfinished work.",
                _pluginName);
            return PluginRuntimeProtocolLineDisposition.QueueClosed;
        }
    }

    private PluginRuntimeProtocolLineDisposition HandleLog(PluginProcessMessage message)
    {
        var payload = PluginProcessMessageSerializer.ReadPayload<PluginProcessLogPayload>(message);
        if (!string.IsNullOrWhiteSpace(payload?.Message))
        {
            _logger.LogInformation("{PluginName}: {PluginLogMessage}", _pluginName, payload.Message);
        }

        return PluginRuntimeProtocolLineDisposition.Log;
    }

    private PluginRuntimeProtocolLineDisposition HandleError(PluginProcessMessage message)
    {
        var payload = PluginProcessMessageSerializer.ReadPayload<PluginProcessErrorPayload>(message);
        _logger.LogWarning(
            "Plugin runtime {PluginName} emitted protocol error for job {ProcessStepId}: {ErrorMessage}",
            _pluginName,
            message.JobId,
            payload?.Message ?? "<missing error payload>");
        return PluginRuntimeProtocolLineDisposition.Error;
    }

    private PluginRuntimeProtocolLineDisposition HandleHeartbeat(PluginProcessMessage message)
    {
        _ = PluginProcessMessageSerializer.ReadPayload<PluginProcessHeartbeatPayload>(message);
        _telemetry.RecordHeartbeat(_pluginName);
        _logger.LogDebug("Plugin runtime {PluginName} heartbeat received.", _pluginName);
        return PluginRuntimeProtocolLineDisposition.Heartbeat;
    }
}

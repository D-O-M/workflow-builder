using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Backend.Database.State.Objects;

namespace Backend.Plugins.Communication;

/// <summary>
/// Minimal invocation envelope exchanged between the engine and nano-service.
/// The envelope omits payload data and instead conveys the identifiers needed
/// for the recipient to hydrate state locally.
/// </summary>
/// <param name="ExecutableBlueprintId">Identifier of the blueprint version being executed.</param>
/// <param name="ProcessId">Identifier of the process being executed.</param>
/// <param name="ProcessStepId">Identifier of the task instance within the workflow.</param>
public sealed record InvocationEnvelope(
    int ExecutableBlueprintId,
    int BlueprintDefinitionId,
    Guid ProcessId,
    Guid ProcessStepId,
    Guid BlueprintNodeId,
    int TaskDefinitionId,
    int TaskVersionId,
    string TaskVersion,
    string PluginName,
    InputHydrationPlan InputHydrationPlan)
{
    public InvocationTelemetryContext? TelemetryContext { get; init; }
}

public sealed record InvocationTelemetryContext(
    string TraceParent,
    string? TraceState)
{
    public static InvocationTelemetryContext? FromActivity(Activity? activity)
    {
        if (activity is null)
        {
            return null;
        }

        var context = activity.Context;
        if (context.TraceId == default || context.SpanId == default)
        {
            return null;
        }

        var traceFlags = (context.TraceFlags & ActivityTraceFlags.Recorded) == ActivityTraceFlags.Recorded
            ? "01"
            : "00";

        return new InvocationTelemetryContext(
            $"00-{context.TraceId}-{context.SpanId}-{traceFlags}",
            string.IsNullOrWhiteSpace(activity.TraceStateString)
                ? null
                : activity.TraceStateString);
    }
}

/// <summary>
/// Helper responsible for serialising <see cref="InvocationEnvelope"/> payloads.
/// </summary>
public static class InvocationEnvelopeSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static ReadOnlyMemory<byte> Serialize(InvocationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    public static InvocationEnvelope Deserialize(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize<InvocationEnvelope>(payload, Options)
            ?? throw new InvalidOperationException("Unable to read invocation envelope payload.");
    }
}

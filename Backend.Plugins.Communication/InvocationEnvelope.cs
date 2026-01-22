using System.Text.Json;

namespace Backend.Plugins.Communication;

public sealed record PreviousTaskDetails(
    Guid PreviousTaskInstanceId,
    string LinkedOutputChannelName);

/// <summary>
/// Minimal invocation envelope exchanged between the engine and nano-service.
/// The envelope omits payload data and instead conveys the identifiers needed
/// for the recipient to hydrate state locally.
/// </summary>
/// <param name="WorkflowInstanceId">Identifier of the workflow instance being executed.</param>
/// <param name="WorkflowTaskInstanceId">Identifier of the task instance within the workflow.</param>
/// <param name="InputChannelName">Name of the input channel to hydrate locally.</param>
public sealed record InvocationEnvelope(
    Guid WorkflowBlueprintId,
    Guid WorkflowInstanceId,
    Guid WorkflowTaskInstanceId,
    PreviousTaskDetails PreviousTaskDetails);

/// <summary>
/// Helper responsible for serialising <see cref="InvocationEnvelope"/> payloads.
/// </summary>
public static class InvocationEnvelopeSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
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

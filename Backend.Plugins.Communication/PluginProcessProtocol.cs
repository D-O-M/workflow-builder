using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Plugins.Communication;

public static class PluginProcessProtocolVersion
{
    public const int Current = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<PluginProcessMessageKind>))]
public enum PluginProcessMessageKind
{
    Ready = 0,
    Result = 1,
    Log = 2,
    Error = 3,
    Heartbeat = 4
}

public sealed record PluginProcessMessage(
    int ProtocolVersion,
    PluginProcessMessageKind Kind,
    Guid? JobId,
    JsonElement? Payload);

public sealed record PluginProcessLogPayload(
    string Message,
    DateTimeOffset Timestamp);

public sealed record PluginProcessErrorPayload(
    string Message,
    string? ExceptionType,
    string? Details,
    DateTimeOffset Timestamp);

public sealed record PluginProcessHeartbeatPayload(DateTimeOffset Timestamp);

public sealed class PluginProcessProtocolException : Exception
{
    public PluginProcessProtocolException(string message)
        : base(message)
    {
    }

    public PluginProcessProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class PluginProcessMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter<PluginProcessMessageKind>(JsonNamingPolicy.CamelCase)
        }
    };

    public static string Serialize(PluginProcessMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateOutbound(message);
        return JsonSerializer.Serialize(message, Options);
    }

    public static PluginProcessMessage DeserializeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new PluginProcessProtocolException("Plugin process protocol frame is empty.");
        }

        if (line.Contains('\n') || line.Contains('\r'))
        {
            throw new PluginProcessProtocolException("Plugin process protocol frames must be single-line JSON.");
        }

        PluginProcessMessage message;
        try
        {
            ValidateRequiredFrameShape(line);
            message = JsonSerializer.Deserialize<PluginProcessMessage>(line, Options)
                ?? throw new PluginProcessProtocolException("Plugin process protocol frame deserialized to null.");
        }
        catch (PluginProcessProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (TryCreateSpecificParseException(line, ex, out var protocolException))
            {
                throw protocolException;
            }

            throw new PluginProcessProtocolException("Plugin process protocol frame is not valid JSON.", ex);
        }

        ValidateInbound(message);
        return message;
    }

    public static PluginProcessMessage Ready() =>
        new(
            PluginProcessProtocolVersion.Current,
            PluginProcessMessageKind.Ready,
            JobId: null,
            Payload: null);

    public static PluginProcessMessage Result(PluginExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new PluginProcessMessage(
            PluginProcessProtocolVersion.Current,
            PluginProcessMessageKind.Result,
            result.JobId,
            JsonSerializer.SerializeToElement(result, Options));
    }

    public static PluginProcessMessage Log(string message) =>
        new(
            PluginProcessProtocolVersion.Current,
            PluginProcessMessageKind.Log,
            JobId: null,
            JsonSerializer.SerializeToElement(
                new PluginProcessLogPayload(message, DateTimeOffset.UtcNow),
                Options));

    public static PluginProcessMessage Error(
        string message,
        Exception? exception = null,
        Guid? jobId = null) =>
        new(
            PluginProcessProtocolVersion.Current,
            PluginProcessMessageKind.Error,
            jobId,
            JsonSerializer.SerializeToElement(
                new PluginProcessErrorPayload(
                    message,
                    exception?.GetType().FullName ?? exception?.GetType().Name,
                    exception?.ToString(),
                    DateTimeOffset.UtcNow),
                Options));

    public static PluginProcessMessage Heartbeat() =>
        new(
            PluginProcessProtocolVersion.Current,
            PluginProcessMessageKind.Heartbeat,
            JobId: null,
            JsonSerializer.SerializeToElement(
                new PluginProcessHeartbeatPayload(DateTimeOffset.UtcNow),
                Options));

    public static PluginExecutionResult ReadResultPayload(PluginProcessMessage message)
    {
        if (message.Kind is not PluginProcessMessageKind.Result)
        {
            throw new PluginProcessProtocolException("Plugin process protocol frame is not a result frame.");
        }

        if (message.Payload is not JsonElement payload)
        {
            throw new PluginProcessProtocolException("Plugin result frame is missing payload.");
        }

        PluginExecutionResult result;
        try
        {
            result = payload.Deserialize<PluginExecutionResult>(Options)
                ?? throw new PluginProcessProtocolException("Plugin result frame payload deserialized to null.");
        }
        catch (PluginProcessProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PluginProcessProtocolException("Plugin result frame payload is not a valid PluginExecutionResult.", ex);
        }

        if (message.JobId is not Guid jobId)
        {
            throw new PluginProcessProtocolException("Plugin result frame is missing jobId.");
        }

        if (jobId != result.JobId)
        {
            throw new PluginProcessProtocolException(
                $"Plugin result frame jobId '{jobId}' does not match payload jobId '{result.JobId}'.");
        }

        return result;
    }

    public static TPayload? ReadPayload<TPayload>(PluginProcessMessage message)
    {
        if (message.Payload is not JsonElement payload)
        {
            return default;
        }

        try
        {
            return payload.Deserialize<TPayload>(Options);
        }
        catch (Exception ex)
        {
            throw new PluginProcessProtocolException(
                $"Plugin process {message.Kind} frame payload is not a valid {typeof(TPayload).Name}.",
                ex);
        }
    }

    private static void ValidateOutbound(PluginProcessMessage message)
    {
        if (message.ProtocolVersion != PluginProcessProtocolVersion.Current)
        {
            throw new PluginProcessProtocolException(
                $"Unsupported plugin process protocol version '{message.ProtocolVersion}'.");
        }
    }

    private static void ValidateInbound(PluginProcessMessage message)
    {
        if (message.ProtocolVersion != PluginProcessProtocolVersion.Current)
        {
            throw new PluginProcessProtocolException(
                $"Unsupported plugin process protocol version '{message.ProtocolVersion}'.");
        }

        if (!Enum.IsDefined(message.Kind))
        {
            throw new PluginProcessProtocolException(
                $"Unsupported plugin process message kind '{message.Kind}'.");
        }
    }

    private static void ValidateRequiredFrameShape(string line)
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            throw new PluginProcessProtocolException("Plugin process protocol frame must be a JSON object.");
        }

        if (!document.RootElement.TryGetProperty("protocolVersion", out var protocolVersionElement))
        {
            throw new PluginProcessProtocolException("Plugin process protocol frame is missing protocolVersion.");
        }

        if (protocolVersionElement.ValueKind is not JsonValueKind.Number)
        {
            throw new PluginProcessProtocolException("Plugin process protocol frame protocolVersion must be a number.");
        }

        if (!document.RootElement.TryGetProperty("kind", out var kindElement))
        {
            throw new PluginProcessProtocolException("Plugin process protocol frame is missing kind.");
        }

        if (kindElement.ValueKind is not JsonValueKind.String)
        {
            throw new PluginProcessProtocolException("Plugin process protocol frame kind must be a string.");
        }
    }

    private static bool TryCreateSpecificParseException(
        string line,
        Exception innerException,
        out PluginProcessProtocolException exception)
    {
        exception = null!;

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind is JsonValueKind.Object &&
                document.RootElement.TryGetProperty("kind", out var kindElement) &&
                kindElement.ValueKind is JsonValueKind.String)
            {
                var kind = kindElement.GetString();
                if (!Enum.TryParse<PluginProcessMessageKind>(kind, ignoreCase: true, out _))
                {
                    exception = new PluginProcessProtocolException(
                        $"Unsupported plugin process message kind '{kind}'.",
                        innerException);
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}

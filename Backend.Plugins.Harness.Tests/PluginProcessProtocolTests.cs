using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Plugins.Communication;
using Backend.Plugins.Harness;
using Shouldly;

namespace Backend.Plugins.Harness.Tests;

public sealed class PluginProcessProtocolTests
{
    [Test]
    public void Serializer_round_trips_ready_log_error_and_heartbeat_frames()
    {
        RoundTrip(PluginProcessMessageSerializer.Ready()).Kind.ShouldBe(PluginProcessMessageKind.Ready);

        var log = RoundTrip(PluginProcessMessageSerializer.Log("hello"));
        log.Kind.ShouldBe(PluginProcessMessageKind.Log);
        PluginProcessMessageSerializer.ReadPayload<PluginProcessLogPayload>(log)!.Message.ShouldBe("hello");

        var error = RoundTrip(PluginProcessMessageSerializer.Error("boom", new InvalidOperationException("boom")));
        error.Kind.ShouldBe(PluginProcessMessageKind.Error);
        PluginProcessMessageSerializer.ReadPayload<PluginProcessErrorPayload>(error)!.Message.ShouldBe("boom");

        RoundTrip(PluginProcessMessageSerializer.Heartbeat()).Kind.ShouldBe(PluginProcessMessageKind.Heartbeat);
    }

    [Test]
    public void Serializer_round_trips_result_payload()
    {
        var result = CreateResult(Guid.NewGuid());

        var message = RoundTrip(PluginProcessMessageSerializer.Result(result));
        var payload = PluginProcessMessageSerializer.ReadResultPayload(message);

        message.Kind.ShouldBe(PluginProcessMessageKind.Result);
        message.JobId.ShouldBe(result.JobId);
        payload.JobId.ShouldBe(result.JobId);
        payload.Succeeded.ShouldBeTrue();
    }

    [Test]
    public void DeserializeLine_rejects_malformed_json()
    {
        Should.Throw<PluginProcessProtocolException>(() =>
            PluginProcessMessageSerializer.DeserializeLine("not-json"));
    }

    [Test]
    public void DeserializeLine_rejects_unknown_kind()
    {
        var exception = Should.Throw<PluginProcessProtocolException>(() =>
            PluginProcessMessageSerializer.DeserializeLine("""{"protocolVersion":1,"kind":"mystery","jobId":null,"payload":null}"""));

        exception.Message.ShouldContain("Unsupported plugin process message kind");
    }

    [Test]
    public void DeserializeLine_rejects_missing_kind()
    {
        var exception = Should.Throw<PluginProcessProtocolException>(() =>
            PluginProcessMessageSerializer.DeserializeLine("""{"protocolVersion":1,"jobId":null,"payload":null}"""));

        exception.Message.ShouldContain("missing kind");
    }

    [Test]
    public void DeserializeLine_rejects_unsupported_protocol_version()
    {
        var exception = Should.Throw<PluginProcessProtocolException>(() =>
            PluginProcessMessageSerializer.DeserializeLine("""{"protocolVersion":99,"kind":"ready","jobId":null,"payload":null}"""));

        exception.Message.ShouldContain("Unsupported plugin process protocol version");
    }

    [Test]
    public void ReadResultPayload_rejects_job_id_mismatch()
    {
        var result = CreateResult(Guid.NewGuid());
        var message = PluginProcessMessageSerializer.Result(result) with
        {
            JobId = Guid.NewGuid()
        };

        Should.Throw<PluginProcessProtocolException>(() =>
            PluginProcessMessageSerializer.ReadResultPayload(message));
    }

    [Test]
    public async Task ProtocolWriter_emits_ready_and_result_frames()
    {
        using var output = new StringWriter();
        using var gate = new SemaphoreSlim(1, 1);
        var writer = new PluginProcessProtocolWriter(output, gate);
        var result = CreateResult(Guid.NewGuid());

        await writer.WriteReadyAsync(CancellationToken.None);
        await writer.WriteResultAsync(result, CancellationToken.None);

        var frames = ReadFrames(output);
        PluginProcessMessageSerializer.DeserializeLine(frames[0]).Kind.ShouldBe(PluginProcessMessageKind.Ready);
        PluginProcessMessageSerializer.ReadResultPayload(
            PluginProcessMessageSerializer.DeserializeLine(frames[1])).JobId.ShouldBe(result.JobId);
    }

    [Test]
    public void LogTextWriter_converts_console_output_to_log_frame()
    {
        using var output = new StringWriter();
        using var gate = new SemaphoreSlim(1, 1);
        var protocolWriter = new PluginProcessProtocolWriter(output, gate);
        using var logWriter = new PluginProcessLogTextWriter(protocolWriter);

        logWriter.WriteLine("plugin chatter");
        logWriter.Flush();

        var frame = PluginProcessMessageSerializer.DeserializeLine(ReadFrames(output)[0]);
        frame.Kind.ShouldBe(PluginProcessMessageKind.Log);
        PluginProcessMessageSerializer.ReadPayload<PluginProcessLogPayload>(frame)!.Message.ShouldBe("plugin chatter");
    }

    [Test]
    public async Task ProtocolWriter_emits_heartbeat_frame()
    {
        using var output = new StringWriter();
        using var gate = new SemaphoreSlim(1, 1);
        var writer = new PluginProcessProtocolWriter(output, gate);

        await writer.WriteHeartbeatAsync(CancellationToken.None);

        PluginProcessMessageSerializer.DeserializeLine(ReadFrames(output)[0])
            .Kind.ShouldBe(PluginProcessMessageKind.Heartbeat);
    }

    private static PluginProcessMessage RoundTrip(PluginProcessMessage message) =>
        PluginProcessMessageSerializer.DeserializeLine(PluginProcessMessageSerializer.Serialize(message));

    private static string[] ReadFrames(StringWriter output) =>
        output.ToString()
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);

    private static PluginExecutionResult CreateResult(Guid jobId) =>
        new(
            jobId,
            Succeeded: true,
            EffectiveInputData: null,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            GlobalState: null,
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow,
            TimingsMs: null,
            ErrorMessage: null);
}

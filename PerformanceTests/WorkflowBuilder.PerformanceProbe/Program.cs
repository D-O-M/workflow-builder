using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Topology;
using Microsoft.AspNetCore.SignalR.Client;

var command = args.FirstOrDefault()?.ToLowerInvariant();
var options = CommandOptions.Parse(args.Skip(1));

return command switch
{
    "completion" => await RunCompletionAsync(options),
    "realtime" => await RunRealtimeAsync(options),
    _ => PrintUsage()
};

static async Task<int> RunCompletionAsync(CommandOptions options)
{
    using var http = CreateHttpClient(options);
    var blueprints = await FindBlueprintsAsync(http, options.BlueprintKeys);
    var totalCount = options.GetInt("count", 25);
    var concurrency = options.GetInt("concurrency", 5);
    var timeout = TimeSpan.FromSeconds(options.GetInt("timeout-seconds", 60));
    var outputPath = options.GetRequired("output");

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
    await using var writer = File.CreateText(outputPath);
    await writer.WriteLineAsync(
        "blueprintKey,processId,startedUtc,completedUtc,status,latencyMs,stepCount," +
        "acceptedUtc,postCompletedUtc,processCreatedUtc,processUpdatedUtc," +
        "firstStepStartedUtc,lastStepCompletedUtc,postLatencyMs,acceptedToPostReturnMs," +
        "queueToFirstStepStartMs,stepDurationMs,terminalObservationLagMs,pollCount");

    var results = new ConcurrentBag<ProbeProcessResult>();
    using var gate = new SemaphoreSlim(concurrency);
    var tasks = Enumerable.Range(0, totalCount).Select(async index =>
    {
        await gate.WaitAsync();
        try
        {
            var blueprint = blueprints[index % blueprints.Count];
            var result = await StartAndWaitForProcessAsync(
                http,
                blueprint,
                index,
                timeout,
                options);
            results.Add(result);
        }
        finally
        {
            gate.Release();
        }
    });

    await Task.WhenAll(tasks);

    foreach (var result in results.OrderBy(candidate => candidate.StartedUtc))
    {
        await writer.WriteLineAsync(
            $"{result.BlueprintKey},{result.ProcessId},{result.StartedUtc:O},{result.CompletedUtc:O},{result.Status},{result.Latency.TotalMilliseconds:0.###},{result.StepCount}," +
            $"{FormatNullableTimestamp(result.AcceptedUtc)},{FormatNullableTimestamp(result.PostCompletedUtc)},{FormatNullableTimestamp(result.ProcessCreatedUtc)},{FormatNullableTimestamp(result.ProcessUpdatedUtc)}," +
            $"{FormatNullableTimestamp(result.FirstStepStartedUtc)},{FormatNullableTimestamp(result.LastStepCompletedUtc)},{FormatNullableMilliseconds(result.PostLatency)}," +
            $"{FormatNullableMilliseconds(result.AcceptedToPostReturnLatency)},{FormatNullableMilliseconds(result.QueueToFirstStepStartLatency)},{FormatNullableMilliseconds(result.StepDuration)}," +
            $"{FormatNullableMilliseconds(result.TerminalObservationLag)},{result.PollCount}");
    }

    var orderedLatencies = results.Select(result => result.Latency.TotalMilliseconds).Order().ToArray();
    var succeeded = results.Count(result => string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase));
    var failed = results.Count - succeeded;
    var p95 = Percentile(orderedLatencies, 0.95);
    Console.WriteLine(
        $"NFT_PROBE_RESULT completion total={results.Count} succeeded={succeeded} failed={failed} p95Ms={p95:0.###} output={outputPath}");

    return 0;
}

static async Task<int> RunRealtimeAsync(CommandOptions options)
{
    using var http = CreateHttpClient(options);
    var config = await http.GetFromJsonAsync<ClientConfig>(WorkflowBuilderTopology.ClientConfig.Route, ProbeJson.Options)
        ?? throw new InvalidOperationException("Unable to read API client config.");
    var blueprint = await FindBlueprintAsync(http, options.BlueprintKey);
    var totalCount = options.GetInt("count", 10);
    var timeout = TimeSpan.FromSeconds(options.GetInt("timeout-seconds", 60));
    var outputPath = options.GetRequired("output");

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
    await using var writer = File.CreateText(outputPath);
    await writer.WriteLineAsync("processId,startedUtc,eventUtc,status,acceptedToEventMs,updateToEventMs");

    var pending = new ConcurrentDictionary<Guid, TaskCompletionSource<RealtimeCompletion>>();
    await using var connection = new HubConnectionBuilder()
        .WithUrl($"{config.SignalRBaseUrl}{WorkflowBuilderTopology.Realtime.HubRoute}", options =>
        {
            options.UseDefaultCredentials = false;
        })
        .WithAutomaticReconnect()
        .Build();

    connection.On<ProcessSummaryUpdatedEvent>(WorkflowBuilderTopology.Realtime.ProcessSummaryUpdatedEvent, evt =>
    {
        if (!IsTerminalStatus(evt.Status))
        {
            return;
        }

        if (pending.TryGetValue(evt.ProcessId, out var signal))
        {
            signal.TrySetResult(new RealtimeCompletion(evt.Status, DateTimeOffset.UtcNow, evt.UpdatedAt));
        }
    });

    await connection.StartAsync();
    await connection.InvokeAsync("SubscribeBlueprint", blueprint.Id);

    var results = new List<RealtimeProbeResult>();
    for (var index = 0; index < totalCount; index++)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var accepted = await StartProcessAsync(http, blueprint, index, options);
        var signal = new TaskCompletionSource<RealtimeCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[accepted.Accepted.ProcessId] = signal;
        await connection.InvokeAsync("SubscribeProcess", accepted.Accepted.ProcessId);

        RealtimeCompletion completion;
        try
        {
            completion = await signal.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            var detail = await TryGetProcessDetailAsync(http, blueprint.Id, accepted.Accepted.ProcessId);
            completion = new RealtimeCompletion($"timeout:{detail?.Status ?? "unknown"}", DateTimeOffset.UtcNow, detail?.UpdatedAt);
        }
        finally
        {
            pending.TryRemove(accepted.Accepted.ProcessId, out _);
        }

        var acceptedToEventLatency = completion.EventUtc - startedUtc;
        var updateToEventLatency = completion.UpdatedAt is null
            ? (TimeSpan?)null
            : completion.EventUtc - completion.UpdatedAt.Value;
        results.Add(new RealtimeProbeResult(
            accepted.Accepted.ProcessId,
            startedUtc,
            completion.EventUtc,
            completion.Status,
            acceptedToEventLatency,
            updateToEventLatency));

        await writer.WriteLineAsync(
            $"{accepted.Accepted.ProcessId},{startedUtc:O},{completion.EventUtc:O},{completion.Status},{acceptedToEventLatency.TotalMilliseconds:0.###},{FormatNullableMilliseconds(updateToEventLatency)}");
    }

    var orderedAcceptedLatencies = results.Select(result => result.AcceptedToEventLatency.TotalMilliseconds).Order().ToArray();
    var orderedUpdateLatencies = results
        .Select(result => result.UpdateToEventLatency?.TotalMilliseconds)
        .Where(value => value is not null)
        .Cast<double>()
        .Order()
        .ToArray();
    var acceptedP95 = Percentile(orderedAcceptedLatencies, 0.95);
    var updateP95 = Percentile(orderedUpdateLatencies, 0.95);
    var timeouts = results.Count(result => result.Status.StartsWith("timeout:", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(
        $"NFT_PROBE_RESULT realtime total={results.Count} timeouts={timeouts} acceptedToEventP95Ms={acceptedP95:0.###} updateToEventP95Ms={FormatNullableMetric(updateP95)} output={outputPath}");

    return 0;
}

static async Task<ProbeProcessResult> StartAndWaitForProcessAsync(
    HttpClient http,
    Blueprint blueprint,
    int index,
    TimeSpan timeout,
    CommandOptions options)
{
    var accepted = await StartProcessAsync(http, blueprint, index, options);
    var completedUtc = accepted.PostCompletedUtc;
    try
    {
        var (detail, pollCount) = await PollProcessUntilTerminalAsync(http, blueprint.Id, accepted.Accepted.ProcessId, timeout);
        completedUtc = DateTimeOffset.UtcNow;

        return ProbeProcessResult.FromDetail(
            blueprint.Key,
            accepted.Accepted.ProcessId,
            completedUtc,
            detail.Status,
            accepted,
            detail,
            pollCount);
    }
    catch (TimeoutException)
    {
        completedUtc = DateTimeOffset.UtcNow;
        var detail = await TryGetProcessDetailAsync(http, blueprint.Id, accepted.Accepted.ProcessId);
        return ProbeProcessResult.FromDetail(
            blueprint.Key,
            accepted.Accepted.ProcessId,
            completedUtc,
            "Timeout",
            accepted,
            detail,
            pollCount: null);
    }
}

static async Task<ProcessStartResult> StartProcessAsync(
    HttpClient http,
    Blueprint blueprint,
    int index,
    CommandOptions options)
{
    var payload = BuildProcessPayload(index, blueprint.Key, options);
    var requestStartedUtc = DateTimeOffset.UtcNow;

    using var response = await http.PostAsJsonAsync(
        $"api/blueprints/{blueprint.Id}/processes",
        payload,
        ProbeJson.Options);
    var postCompletedUtc = DateTimeOffset.UtcNow;
    var body = await response.Content.ReadAsStringAsync();
    response.EnsureSuccessStatusCode();

    var accepted = JsonSerializer.Deserialize<ProcessAccepted>(body, ProbeJson.Options)
        ?? throw new InvalidOperationException("Process start response was empty.");
    return new ProcessStartResult(
        accepted,
        requestStartedUtc,
        postCompletedUtc,
        postCompletedUtc - requestStartedUtc);
}

static object BuildProcessPayload(int index, string blueprintKey, CommandOptions options)
{
    var payloadPath = options.Get("payload", string.Empty);
    if (!string.IsNullOrWhiteSpace(payloadPath))
    {
        using var payload = JsonDocument.Parse(File.ReadAllText(payloadPath));
        return payload.RootElement.Clone();
    }

    var correlationKey = $"nft-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}-{index}";
    var nodeOverrides = string.Equals(
        blueprintKey,
        "nft-ceiling-fanout-join",
        StringComparison.OrdinalIgnoreCase)
        ? new Dictionary<string, Dictionary<string, object>>
        {
            ["10000000-0000-0000-0000-000000000005"] = new()
            {
                ["Mode"] = "runtime-nft"
            },
            ["10000000-0000-0000-0000-000000000006"] = new()
            {
                ["ExpectedMinimumScore"] = 1
            }
        }
        : [];

    if (string.Equals(blueprintKey, "nft-linear-alpha-single-step", StringComparison.OrdinalIgnoreCase))
    {
        return new
        {
            globalState = new
            {
                runLabel = "local-nft",
                stepsCompleted = 0,
                totalScore = 0,
                lastStage = "",
                traceEnabled = false
            },
            initialTaskInput = new
            {
                correlationKey,
                payloadToken = $"alpha-token-{index % 1024:D4}",
                seedScore = 10 + index % 7,
                branchName = "alpha-direct",
                branchWeight = 1 + index % 3,
                delayMs = 0
            },
            nodeOverrides
        };
    }

    if (blueprintKey.StartsWith("nft-linear-lane-", StringComparison.OrdinalIgnoreCase))
    {
        return new
        {
            globalState = new
            {
                runLabel = "local-nft",
                stepsCompleted = 0,
                totalScore = 0,
                lastStage = "",
                traceEnabled = false
            },
            initialTaskInput = new
            {
                correlationKey,
                payloadToken = $"lane-token-{index % 1024:D4}",
                seedScore = 10 + index % 7,
                laneName = "linear-lane",
                laneWeight = 1 + index % 3,
                delayMs = 0
            },
            nodeOverrides
        };
    }

    if (blueprintKey.StartsWith("nft-score-collection-iterator", StringComparison.OrdinalIgnoreCase))
    {
        return new
        {
            globalState = new
            {
                runLabel = "local-nft",
                stepsCompleted = 0,
                totalScore = 0,
                lastStage = "",
                traceEnabled = false
            },
            initialTaskInput = new
            {
                correlationKey,
                itemCount = options.GetInt("collection-item-count", 10),
                delayMs = 0
            },
            nodeOverrides
        };
    }

    return new
    {
        globalState = new
        {
            runLabel = "local-nft",
            stepsCompleted = 0,
            totalScore = 0,
            lastStage = "",
            traceEnabled = false
        },
        initialTaskInput = new
        {
            correlationKey,
            payloadSize = 256 + index % 512,
            baseScore = 10 + index % 7,
            region = "local",
            delayMs = 0
        },
        nodeOverrides
    };
}

static async Task<(ProcessDetail Detail, int PollCount)> PollProcessUntilTerminalAsync(
    HttpClient http,
    int blueprintDefinitionId,
    Guid processId,
    TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    var pollCount = 0;

    while (DateTimeOffset.UtcNow < deadline)
    {
        pollCount++;
        var detail = await http.GetFromJsonAsync<ProcessDetail>(
            $"api/blueprints/{blueprintDefinitionId}/processes/{processId}",
            ProbeJson.Options);

        if (detail is not null && IsTerminalStatus(detail.Status))
        {
            return (detail, pollCount);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }

    throw new TimeoutException($"Process {processId} did not reach a terminal state within {timeout}.");
}

static async Task<ProcessDetail?> TryGetProcessDetailAsync(
    HttpClient http,
    int blueprintDefinitionId,
    Guid processId)
{
    try
    {
        return await http.GetFromJsonAsync<ProcessDetail>(
            $"api/blueprints/{blueprintDefinitionId}/processes/{processId}",
            ProbeJson.Options);
    }
    catch (HttpRequestException)
    {
        return null;
    }
    catch (TaskCanceledException)
    {
        return null;
    }
}

static async Task<Blueprint> FindBlueprintAsync(HttpClient http, string blueprintKey)
{
    return (await FindBlueprintsAsync(http, [blueprintKey]))[0];
}

static async Task<List<Blueprint>> FindBlueprintsAsync(HttpClient http, IReadOnlyList<string> blueprintKeys)
{
    var blueprints = await http.GetFromJsonAsync<List<Blueprint>>("api/blueprints", ProbeJson.Options)
        ?? [];

    var selected = new List<Blueprint>(blueprintKeys.Count);
    foreach (var blueprintKey in blueprintKeys)
    {
        selected.Add(
            blueprints.FirstOrDefault(blueprint =>
                string.Equals(blueprint.Key, blueprintKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Blueprint '{blueprintKey}' was not found."));
    }

    return selected;
}

static HttpClient CreateHttpClient(CommandOptions options)
{
    var apiBaseUrl = options.GetRequired("api-base-url").TrimEnd('/') + "/";
    return new HttpClient
    {
        BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute),
        Timeout = TimeSpan.FromSeconds(options.GetInt("http-timeout-seconds", 100))
    };
}

static bool IsTerminalStatus(string status) =>
    string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase);

static double Percentile(double[] orderedValues, double percentile)
{
    if (orderedValues.Length == 0)
    {
        return double.NaN;
    }

    var index = (int)Math.Ceiling(percentile * orderedValues.Length) - 1;
    return orderedValues[Math.Clamp(index, 0, orderedValues.Length - 1)];
}

static string FormatNullableMilliseconds(TimeSpan? value) =>
    value is null ? "" : value.Value.TotalMilliseconds.ToString("0.###");

static string FormatNullableTimestamp(DateTimeOffset? value) =>
    value is null ? "" : value.Value.ToString("O");

static string FormatNullableMetric(double value) =>
    double.IsNaN(value) ? "n/a" : value.ToString("0.###");

static int PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  WorkflowBuilder.PerformanceProbe completion --api-base-url <url> --blueprint-key <key> --output <csv> [--count 25] [--concurrency 5] [--payload <json>]");
    Console.Error.WriteLine("  WorkflowBuilder.PerformanceProbe completion --api-base-url <url> --blueprint-keys <key1,key2> --output <csv> [--count 25] [--concurrency 5]");
    Console.Error.WriteLine("  WorkflowBuilder.PerformanceProbe realtime --api-base-url <url> --blueprint-key <key> --output <csv> [--count 10] [--payload <json>]");
    return 2;
}

sealed record CommandOptions(IReadOnlyDictionary<string, string> Values)
{
    public static CommandOptions Parse(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pendingKey = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                pendingKey = arg[2..];
                values[pendingKey] = "true";
                continue;
            }

            if (pendingKey is null)
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }

            values[pendingKey] = arg;
            pendingKey = null;
        }

        return new CommandOptions(values);
    }

    public IReadOnlyList<string> BlueprintKeys =>
        Get("blueprint-keys", Get("blueprint-key", "nft-ceiling-fanout-join"))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string BlueprintKey => BlueprintKeys[0];

    public string GetRequired(string key) =>
        Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option --{key}.");

    public string Get(string key, string fallback) =>
        Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    public int GetInt(string key, int fallback) =>
        Values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : fallback;
}

sealed record Blueprint(int Id, string Key, string Name);

sealed record ProcessAccepted(Guid ProcessId, int BlueprintId, DateTimeOffset AcceptedAt);

sealed record ProcessStartResult(
    ProcessAccepted Accepted,
    DateTimeOffset RequestStartedUtc,
    DateTimeOffset PostCompletedUtc,
    TimeSpan PostLatency);

sealed record ProcessDetail(
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    List<ProcessStep> Nodes);

sealed record ProcessStep(
    Guid InstanceId,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? FailedAt);

sealed record ClientConfig(string ApiBaseUrl, string SignalRBaseUrl, string GrafanaBaseUrl);

sealed record ProcessSummaryUpdatedEvent(Guid ProcessId, string Status, DateTimeOffset? UpdatedAt);

sealed record ProbeProcessResult(
    string BlueprintKey,
    Guid ProcessId,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string Status,
    TimeSpan Latency,
    int StepCount,
    DateTimeOffset? AcceptedUtc,
    DateTimeOffset? PostCompletedUtc,
    DateTimeOffset? ProcessCreatedUtc,
    DateTimeOffset? ProcessUpdatedUtc,
    DateTimeOffset? FirstStepStartedUtc,
    DateTimeOffset? LastStepCompletedUtc,
    TimeSpan? PostLatency,
    TimeSpan? AcceptedToPostReturnLatency,
    TimeSpan? QueueToFirstStepStartLatency,
    TimeSpan? StepDuration,
    TimeSpan? TerminalObservationLag,
    int? PollCount)
{
    public static ProbeProcessResult FromDetail(
        string blueprintKey,
        Guid processId,
        DateTimeOffset completedUtc,
        string status,
        ProcessStartResult accepted,
        ProcessDetail? detail,
        int? pollCount)
    {
        var terminalStepTimes = detail?.Nodes
            .Select(step => step.CompletedAt ?? step.FailedAt)
            .Where(timestamp => timestamp.HasValue)
            .Select(timestamp => timestamp!.Value)
            .ToArray() ?? [];
        var startedStepTimes = detail?.Nodes
            .Select(step => step.StartedAt)
            .Where(timestamp => timestamp.HasValue)
            .Select(timestamp => timestamp!.Value)
            .ToArray() ?? [];
        var firstStepStartedUtc = startedStepTimes.Length == 0
            ? (DateTimeOffset?)null
            : startedStepTimes.Min();
        var lastStepCompletedUtc = terminalStepTimes.Length == 0
            ? (DateTimeOffset?)null
            : terminalStepTimes.Max();
        var processCreatedUtc = detail?.CreatedAt;
        var processUpdatedUtc = detail?.UpdatedAt;

        return new ProbeProcessResult(
            blueprintKey,
            processId,
            accepted.RequestStartedUtc,
            completedUtc,
            status,
            completedUtc - accepted.RequestStartedUtc,
            detail?.Nodes.Count ?? 0,
            accepted.Accepted.AcceptedAt,
            accepted.PostCompletedUtc,
            processCreatedUtc,
            processUpdatedUtc,
            firstStepStartedUtc,
            lastStepCompletedUtc,
            accepted.PostLatency,
            accepted.PostCompletedUtc - accepted.Accepted.AcceptedAt,
            firstStepStartedUtc.HasValue && processCreatedUtc.HasValue
                ? firstStepStartedUtc.Value - processCreatedUtc.Value
                : null,
            firstStepStartedUtc.HasValue && lastStepCompletedUtc.HasValue
                ? lastStepCompletedUtc.Value - firstStepStartedUtc.Value
                : null,
            processUpdatedUtc.HasValue
                ? completedUtc - processUpdatedUtc.Value
                : null,
            pollCount);
    }
}

sealed record RealtimeCompletion(string Status, DateTimeOffset EventUtc, DateTimeOffset? UpdatedAt);

sealed record RealtimeProbeResult(
    Guid ProcessId,
    DateTimeOffset StartedUtc,
    DateTimeOffset EventUtc,
    string Status,
    TimeSpan AcceptedToEventLatency,
    TimeSpan? UpdateToEventLatency);

static class ProbeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

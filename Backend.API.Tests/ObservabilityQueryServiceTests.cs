using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Observability;
using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Backend.API.Tests;

public sealed class ObservabilityQueryServiceTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Scoped_plugin_logs_are_found_without_identifiers_in_the_message(
        bool legacyIdentifiers,
        bool indexedIdentifiers)
    {
        var processId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var processLabel = legacyIdentifiers ? "workflow_instance_id" : "process_id";
        var stepLabel = legacyIdentifiers ? "workflow_task_instance_id" : "process_step_id";
        var identifiers = new Dictionary<string, string>
        {
            [processLabel] = processId.ToString("D"),
            [stepLabel] = stepId.ToString("D"),
            ["blueprint_node_id"] = nodeId.ToString("D"),
            ["plugin_name"] = "Example plugin",
            ["detected_level"] = "info",
        };
        // Old indexed logs need not have service_name. Native OTLP logs index the
        // service and keep invocation identifiers in per-entry structured metadata.
        var log = indexedIdentifiers
            ? new TestLog("Processing input", identifiers, [])
            : new TestLog("Processing input", new() { ["service_name"] = "example-plugin" }, identifiers);
        var unrelatedLog = new TestLog("Unrelated process", new() { ["service_name"] = "example-plugin" }, new()
        {
            [processLabel] = Guid.NewGuid().ToString("D"),
            [stepLabel] = Guid.NewGuid().ToString("D"),
        });
        using var httpClient = new HttpClient(new LokiHandler(log, unrelatedLog)) { BaseAddress = new Uri("http://loki.test") };
        using var dbContext = new BlueprintDbContext(new DbContextOptionsBuilder<BlueprintDbContext>().Options);
        var service = CreateService(dbContext, httpClient);

        var processLogs = await service.GetProcessLogsAsync(processId, null, null, 100, null, CancellationToken.None);
        var stepLogs = await service.GetProcessStepLogsByRuntimeIdAsync(processId, stepId, null, null, 100, null, CancellationToken.None);

        var processEntry = processLogs.Entries.ShouldHaveSingleItem();
        processEntry.Message.ShouldBe("Processing input");
        processEntry.ProcessStepId.ShouldBe(stepId);
        processEntry.BlueprintNodeId.ShouldBe(nodeId);
        processEntry.PluginName.ShouldBe("Example plugin");
        processEntry.Level.ShouldBe("info");
        processLogs.NextCursor.ShouldBe(processEntry.Cursor);
        var stepEntry = stepLogs.Entries.ShouldHaveSingleItem();
        stepEntry.Message.ShouldBe("Processing input");
        stepEntry.Level.ShouldBe("info");
        if (!indexedIdentifiers)
        {
            processEntry.ServiceName.ShouldBe("example-plugin");
            stepEntry.ServiceName.ShouldBe("example-plugin");
        }
    }

    [Test]
    public async Task Unscoped_logs_can_still_be_found_by_identifiers_in_the_message()
    {
        var processId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var message = $"Process {processId:D}, step {stepId:D}: completed";
        var log = new TestLog(message, new() { ["service_name"] = "backend-engine" }, []);
        using var httpClient = new HttpClient(new LokiHandler(log)) { BaseAddress = new Uri("http://loki.test") };
        using var dbContext = new BlueprintDbContext(new DbContextOptionsBuilder<BlueprintDbContext>().Options);
        var service = CreateService(dbContext, httpClient);

        var processLogs = await service.GetProcessLogsAsync(processId, null, null, 100, null, CancellationToken.None);
        var stepLogs = await service.GetProcessStepLogsByRuntimeIdAsync(processId, stepId, null, null, 100, null, CancellationToken.None);

        processLogs.Entries.ShouldHaveSingleItem().Message.ShouldBe(message);
        stepLogs.Entries.ShouldHaveSingleItem().Message.ShouldBe(message);
    }

    private static ObservabilityQueryService CreateService(BlueprintDbContext dbContext, HttpClient httpClient) =>
        new(dbContext, new LokiQueryClient(httpClient), new PrometheusQueryClient(httpClient));

    private sealed record TestLog(
        string Message,
        Dictionary<string, string> IndexedLabels,
        Dictionary<string, string> Metadata);

    // Models Loki's distinction between index selectors and pipeline metadata
    // filters. Response labels follow an actual native OTLP query response.
    private sealed class LokiHandler(params TestLog[] logs) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var queryParameter = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Single(parameter => parameter.StartsWith("query=", StringComparison.Ordinal));
            var query = Uri.UnescapeDataString(queryParameter["query=".Length..]);
            var selectorEnd = query.IndexOf('}');
            var selector = query[1..selectorEnd];
            var pipeline = query[(selectorEnd + 1)..].Trim();
            var timestamp = LokiQueryClient.ToUnixNanoseconds(DateTimeOffset.UtcNow);
            var matchingLogs = logs.Where(log =>
            {
                if (!MatchLabels(selector, log.IndexedLabels, requireAll: true))
                {
                    return false;
                }

                if (pipeline.StartsWith("|=", StringComparison.Ordinal))
                {
                    return log.Message.Contains(pipeline[2..].Trim().Trim('"'), StringComparison.Ordinal);
                }

                var returnedLabels = GetReturnedLabels(log);
                return pipeline.Length == 0 || MatchLabels(pipeline, returnedLabels, requireAll: false);
            });
            var result = matchingLogs.Select(log => new
            {
                stream = GetReturnedLabels(log),
                values = new[] { new[] { timestamp, log.Message } },
            });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    status = "success",
                    data = new { resultType = "streams", result },
                }), Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }

        private static Dictionary<string, string> GetReturnedLabels(TestLog log) =>
            log.IndexedLabels.Concat(log.Metadata).ToDictionary(label => label.Key, label => label.Value);

        private static bool MatchLabels(string expression, IReadOnlyDictionary<string, string> labels, bool requireAll)
        {
            var matchers = Regex.Matches(expression, "(?<name>[a-z_]+)(?<operator>=~|=)\"(?<value>[^\"]*)\"");
            var matches = matchers.Select(matcher =>
            {
                if (!labels.TryGetValue(matcher.Groups["name"].Value, out var value))
                {
                    return false;
                }

                var expected = matcher.Groups["value"].Value;
                return matcher.Groups["operator"].Value == "=~"
                    ? Regex.IsMatch(value, $"^(?:{expected})$")
                    : value == expected;
            });
            return requireAll ? matches.All(match => match) : matches.Any(match => match);
        }
    }
}

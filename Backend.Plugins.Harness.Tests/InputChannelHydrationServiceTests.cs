using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Plugins.Communication;
using Backend.Plugins.Harness;
using Shouldly;

namespace Backend.Plugins.Harness.Tests;

public sealed class InputChannelHydrationServiceTests
{
    [Test]
    public async Task HydrateAsync_Applies_mappings_design_time_overrides_and_runtime_overrides_in_order()
    {
        var processId = Guid.NewGuid();
        var sourceStepId = Guid.NewGuid();
        var sourceStep = new ProcessStepRecord
        {
            Id = sourceStepId.ToString(),
            ProcessId = processId.ToString(),
            BlueprintId = "100",
            BlueprintNodeId = Guid.NewGuid().ToString(),
            Status = ProcessStepStatus.Completed,
            OutputData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["Results"] = Json(new
                {
                    town = "mapped town",
                    score = 12,
                    unused = "ignored"
                })
            }
        };

        var service = new InputChannelHydrationService<HydratedInput>(
            new MemoryPluginDocumentStore<ProcessStepRecord>(sourceStep.Id, processId.ToString(), sourceStep));

        var envelope = new InvocationEnvelope(
            ExecutableBlueprintId: 100,
            BlueprintDefinitionId: 10,
            ProcessId: processId,
            ProcessStepId: Guid.NewGuid(),
            BlueprintNodeId: Guid.NewGuid(),
            TaskDefinitionId: 7,
            TaskVersionId: 70,
            TaskVersion: "1.0.0",
            PluginName: "HydrationProbe",
            InputHydrationPlan: new InputHydrationPlan
            {
                InputChannelName = "Input",
                InitialInput = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["Town"] = Json("initial town"),
                    ["Score"] = Json(1)
                },
                Sources =
                [
                    new InputHydrationSource
                    {
                        BindingId = "source-to-probe",
                        SourceProcessStepId = sourceStepId.ToString(),
                        SourceOutputChannelName = "Results",
                        PropertyMappings =
                        [
                            new InputHydrationPropertyMapping
                            {
                                SourceProperty = "Town",
                                TargetProperty = "Town"
                            },
                            new InputHydrationPropertyMapping
                            {
                                SourceProperty = "Score",
                                TargetProperty = "Score"
                            }
                        ]
                    }
                ],
                DesignTimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["Score"] = Json(20),
                    ["Mode"] = Json("design")
                },
                RuntimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["Town"] = Json("runtime town")
                }
            });

        var result = await service.HydrateAsync(envelope, "Input", CancellationToken.None);

        result.Value.Town.ShouldBe("runtime town");
        result.Value.Score.ShouldBe(20);
        result.Value.Mode.ShouldBe("design");

        result.EffectiveInput.GetProperty("Town").GetString().ShouldBe("runtime town");
        result.EffectiveInput.GetProperty("Score").GetInt32().ShouldBe(20);
        result.EffectiveInput.GetProperty("Mode").GetString().ShouldBe("design");
        result.EffectiveInput.TryGetProperty("unused", out _).ShouldBeFalse();
    }

    [Test]
    public async Task HydrateAsync_Fails_clearly_when_a_mapped_source_property_is_missing()
    {
        var processId = Guid.NewGuid();
        var sourceStepId = Guid.NewGuid();
        var sourceStep = new ProcessStepRecord
        {
            Id = sourceStepId.ToString(),
            ProcessId = processId.ToString(),
            BlueprintId = "100",
            BlueprintNodeId = Guid.NewGuid().ToString(),
            Status = ProcessStepStatus.Completed,
            OutputData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["Results"] = Json(new
                {
                    Town = "mapped town"
                })
            }
        };

        var service = new InputChannelHydrationService<HydratedInput>(
            new MemoryPluginDocumentStore<ProcessStepRecord>(sourceStep.Id, processId.ToString(), sourceStep));

        var envelope = new InvocationEnvelope(
            ExecutableBlueprintId: 100,
            BlueprintDefinitionId: 10,
            ProcessId: processId,
            ProcessStepId: Guid.NewGuid(),
            BlueprintNodeId: Guid.NewGuid(),
            TaskDefinitionId: 7,
            TaskVersionId: 70,
            TaskVersion: "1.0.0",
            PluginName: "HydrationProbe",
            InputHydrationPlan: new InputHydrationPlan
            {
                InputChannelName = "Input",
                Sources =
                [
                    new InputHydrationSource
                    {
                        BindingId = "source-to-probe",
                        SourceProcessStepId = sourceStepId.ToString(),
                        SourceOutputChannelName = "Results",
                        PropertyMappings =
                        [
                            new InputHydrationPropertyMapping
                            {
                                SourceProperty = "Score",
                                TargetProperty = "Score"
                            }
                        ]
                    }
                ]
            });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => service.HydrateAsync(envelope, "Input", CancellationToken.None));

        ex.Message.ShouldContain("Source property 'Score'");
        ex.Message.ShouldContain("target property 'Score'");
    }

    private static JsonElement Json<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private sealed class HydratedInput
    {
        public string? Town { get; set; }
        public int Score { get; set; }
        public string? Mode { get; set; }
    }

    private sealed class MemoryPluginDocumentStore<TDocument> : IPluginDocumentStore<TDocument>
        where TDocument : class
    {
        private readonly string _documentId;
        private readonly string _partitionKey;
        private readonly TDocument _document;

        public MemoryPluginDocumentStore(string documentId, string partitionKey, TDocument document)
        {
            _documentId = documentId;
            _partitionKey = partitionKey;
            _document = document;
        }

        public Task<TDocument?> GetAsync(
            string documentId,
            string partitionKey,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                string.Equals(documentId, _documentId, StringComparison.Ordinal) &&
                string.Equals(partitionKey, _partitionKey, StringComparison.Ordinal)
                    ? _document
                    : null);
        }
    }
}

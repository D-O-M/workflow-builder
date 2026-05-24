using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Plugins.Communication;
using Backend.Plugins.Harness;
using Shouldly;

[assembly: AssemblyMetadata("WorkflowBuilder.PluginInputMaterializerProvider", "Backend.Plugins.Harness.Tests.DiscoveredInputMaterializerProvider")]

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

        var service = CreateService(sourceStep, processId);

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

        var service = CreateService(sourceStep, processId);

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

    [Test]
    public async Task HydrateAsync_uses_fast_materializer_for_small_input_and_preserves_effective_input()
    {
        var processId = Guid.NewGuid();
        var sourceStep = CreateSourceStep(processId, "Result", new
        {
            Name = "small",
            Score = 11
        });
        var materializer = new SmallInputMaterializer();
        var service = CreateService(
            new MemoryPluginDocumentStore<ProcessStepRecord>(sourceStep.Id, processId.ToString(), sourceStep),
            materializer);

        var result = await service.HydrateAsync(
            CreateEnvelope(
                processId,
                channelName: "Input",
                sources:
                [
                    CreateSource(sourceStep, "Result",
                    [
                        ("Name", "Name"),
                        ("Score", "Score")
                    ])
                ]),
            "Input",
            CancellationToken.None);

        materializer.CallCount.ShouldBe(1);
        result.Value.Name.ShouldBe("small");
        result.Value.Score.ShouldBe(11);
        result.EffectiveInput.GetProperty("Name").GetString().ShouldBe("small");
        result.EffectiveInput.GetProperty("Score").GetInt32().ShouldBe(11);
    }

    [Test]
    public async Task HydrateAsync_uses_fast_materializer_for_wide_input()
    {
        var processId = Guid.NewGuid();
        var materializer = new WideInputMaterializer();
        var service = CreateService(
            new MemoryPluginDocumentStore<ProcessStepRecord>(),
            materializer);

        var result = await service.HydrateAsync(
            CreateEnvelope(
                processId,
                channelName: "Input",
                initialInput: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["Text01"] = Json("one"),
                    ["Text02"] = Json("two"),
                    ["Text03"] = Json("three"),
                    ["Number01"] = Json(1),
                    ["Number02"] = Json(2),
                    ["Number03"] = Json(3),
                    ["Number04"] = Json(4),
                    ["Number05"] = Json(5),
                    ["Flag01"] = Json(true),
                    ["Flag02"] = Json(false),
                    ["Amount01"] = Json(12.5m),
                    ["Amount02"] = Json(25.5m)
                }),
            "Input",
            CancellationToken.None);

        materializer.CallCount.ShouldBe(1);
        result.Value.Text01.ShouldBe("one");
        result.Value.Text03.ShouldBe("three");
        result.Value.Number05.ShouldBe(5);
        result.Value.Flag01.ShouldBeTrue();
        result.Value.Flag02.ShouldBeFalse();
        result.Value.Amount02.ShouldBe(25.5m);
        result.EffectiveInput.EnumerateObject().Count().ShouldBe(12);
    }

    [Test]
    public async Task HydrateAsync_materializes_joined_input_from_multiple_sources()
    {
        var processId = Guid.NewGuid();
        var firstSource = CreateSourceStep(processId, "FirstOutput", new
        {
            FirstName = "north",
            FirstScore = 10
        });
        var secondSource = CreateSourceStep(processId, "SecondOutput", new
        {
            SecondName = "south",
            SecondScore = 20
        });
        var materializer = new JoinedInputMaterializer();
        var service = CreateService(
            new MemoryPluginDocumentStore<ProcessStepRecord>(
                (firstSource.Id, processId.ToString(), firstSource),
                (secondSource.Id, processId.ToString(), secondSource)),
            materializer);

        var result = await service.HydrateAsync(
            CreateEnvelope(
                processId,
                channelName: "Input",
                sources:
                [
                    CreateSource(firstSource, "FirstOutput",
                    [
                        ("FirstName", "FirstName"),
                        ("FirstScore", "FirstScore")
                    ]),
                    CreateSource(secondSource, "SecondOutput",
                    [
                        ("SecondName", "SecondName"),
                        ("SecondScore", "SecondScore")
                    ])
                ]),
            "Input",
            CancellationToken.None);

        materializer.CallCount.ShouldBe(1);
        result.Value.FirstName.ShouldBe("north");
        result.Value.SecondName.ShouldBe("south");
        result.Value.TotalScore.ShouldBe(30);
        result.EffectiveInput.GetProperty("FirstName").GetString().ShouldBe("north");
        result.EffectiveInput.GetProperty("SecondScore").GetInt32().ShouldBe(20);
    }

    [Test]
    public void Generated_input_materializer_metadata_is_discoverable_from_plugin_assembly()
    {
        var materializer = PluginInputMaterialization.ResolveMaterializer<DiscoveredInput>(typeof(DiscoveredPlugin));

        materializer.ShouldNotBeNull();
        var input = materializer.Materialize(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["Name"] = Json("discovered"),
            ["Score"] = Json(77)
        });

        input.Name.ShouldBe("discovered");
        input.Score.ShouldBe(77);
    }

    [Test]
    public void Input_hydration_source_uses_source_generated_type_info_and_fast_materializer_path()
    {
        var source = File.ReadAllText(FindRepoFile("Backend.Plugins.Harness", "InputChannelHydrationService.cs"));

        source.ShouldContain("Deserialize(_jsonTypeInfo)");
        source.ShouldContain("_materializer.Materialize(merged)");
        source.ShouldNotContain("Deserialize<TChannelType>(JsonSerializerOptions.Web)");
        source.ShouldNotContain("JsonSerializer.SerializeToElement(merged, JsonSerializerOptions.Web)");
        source.ShouldNotContain("EnumerateObject().ToList().ToDictionary");
    }

    private static JsonElement Json<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private static InputChannelHydrationService<HydratedInput> CreateService(
        ProcessStepRecord sourceStep,
        Guid processId) =>
        new(
            new MemoryPluginDocumentStore<ProcessStepRecord>(sourceStep.Id, processId.ToString(), sourceStep),
            PluginJsonSerialization.RequireTypeInfo<HydratedInput>(
                InputHydrationJsonContext.Default,
                "test input channel"));

    private static InputChannelHydrationService<TInput> CreateService<TInput>(
        IPluginDocumentStore<ProcessStepRecord> store,
        IPluginInputMaterializer<TInput>? materializer = null)
        where TInput : class =>
        new(
            store,
            PluginJsonSerialization.RequireTypeInfo<TInput>(
                InputHydrationJsonContext.Default,
                "test input channel"),
            materializer);

    private static InvocationEnvelope CreateEnvelope(
        Guid processId,
        string channelName,
        IReadOnlyDictionary<string, JsonElement>? initialInput = null,
        IReadOnlyList<InputHydrationSource>? sources = null) =>
        new(
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
                InputChannelName = channelName,
                InitialInput = initialInput is null
                    ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    : new Dictionary<string, JsonElement>(initialInput, StringComparer.Ordinal),
                Sources = sources?.ToList() ?? []
            });

    private static ProcessStepRecord CreateSourceStep(
        Guid processId,
        string outputChannelName,
        object outputPayload) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            ProcessId = processId.ToString(),
            BlueprintId = "100",
            BlueprintNodeId = Guid.NewGuid().ToString(),
            Status = ProcessStepStatus.Completed,
            OutputData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [outputChannelName] = Json(outputPayload)
            }
        };

    private static InputHydrationSource CreateSource(
        ProcessStepRecord sourceStep,
        string outputChannelName,
        IReadOnlyList<(string Source, string Target)> mappings) =>
        new()
        {
            BindingId = Guid.NewGuid().ToString(),
            SourceProcessStepId = sourceStep.Id,
            SourceOutputChannelName = outputChannelName,
            PropertyMappings = mappings
                .Select(mapping => new InputHydrationPropertyMapping
                {
                    SourceProperty = mapping.Source,
                    TargetProperty = mapping.Target
                })
                .ToList()
        };

    private static string FindRepoFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. pathParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to find repo file '{Path.Combine(pathParts)}'.");
    }

    [PluginJsonSerializable]
    public sealed class HydratedInput
    {
        public string? Town { get; set; }
        public int Score { get; set; }
        public string? Mode { get; set; }
    }

    [PluginJsonSerializable]
    public sealed class SmallInput
    {
        public string? Name { get; set; }
        public int Score { get; set; }
    }

    [PluginJsonSerializable]
    public sealed class WideInput
    {
        public string? Text01 { get; set; }
        public string? Text02 { get; set; }
        public string? Text03 { get; set; }
        public int Number01 { get; set; }
        public int Number02 { get; set; }
        public int Number03 { get; set; }
        public int Number04 { get; set; }
        public int Number05 { get; set; }
        public bool Flag01 { get; set; }
        public bool Flag02 { get; set; }
        public decimal Amount01 { get; set; }
        public decimal Amount02 { get; set; }
    }

    [PluginJsonSerializable]
    public sealed class JoinedInput
    {
        public string? FirstName { get; set; }
        public string? SecondName { get; set; }
        public int TotalScore { get; set; }
    }

    [PluginJsonSerializable]
    public sealed class DiscoveredInput
    {
        public string? Name { get; set; }
        public int Score { get; set; }
    }

    private sealed class DiscoveredPlugin : IPlugin
    {
        public DiscoveredInput? Input { get; set; }
        public Task Execute() => Task.CompletedTask;
    }

    private sealed class SmallInputMaterializer : IPluginInputMaterializer<SmallInput>
    {
        public int CallCount { get; private set; }

        public SmallInput Materialize(IReadOnlyDictionary<string, JsonElement> values)
        {
            CallCount++;
            return new SmallInput
            {
                Name = PluginInputMaterializerUtilities.TryGetValue(values, nameof(SmallInput.Name), out var name)
                    ? PluginInputMaterializerUtilities.ReadString(name)
                    : null,
                Score = PluginInputMaterializerUtilities.TryGetValue(values, nameof(SmallInput.Score), out var score)
                    ? PluginInputMaterializerUtilities.ReadInt32(score)
                    : 0
            };
        }
    }

    private sealed class WideInputMaterializer : IPluginInputMaterializer<WideInput>
    {
        public int CallCount { get; private set; }

        public WideInput Materialize(IReadOnlyDictionary<string, JsonElement> values)
        {
            CallCount++;
            return new WideInput
            {
                Text01 = ReadString(values, nameof(WideInput.Text01)),
                Text02 = ReadString(values, nameof(WideInput.Text02)),
                Text03 = ReadString(values, nameof(WideInput.Text03)),
                Number01 = ReadInt32(values, nameof(WideInput.Number01)),
                Number02 = ReadInt32(values, nameof(WideInput.Number02)),
                Number03 = ReadInt32(values, nameof(WideInput.Number03)),
                Number04 = ReadInt32(values, nameof(WideInput.Number04)),
                Number05 = ReadInt32(values, nameof(WideInput.Number05)),
                Flag01 = ReadBoolean(values, nameof(WideInput.Flag01)),
                Flag02 = ReadBoolean(values, nameof(WideInput.Flag02)),
                Amount01 = ReadDecimal(values, nameof(WideInput.Amount01)),
                Amount02 = ReadDecimal(values, nameof(WideInput.Amount02))
            };
        }
    }

    private sealed class JoinedInputMaterializer : IPluginInputMaterializer<JoinedInput>
    {
        public int CallCount { get; private set; }

        public JoinedInput Materialize(IReadOnlyDictionary<string, JsonElement> values)
        {
            CallCount++;
            var firstScore = ReadInt32(values, "FirstScore");
            var secondScore = ReadInt32(values, "SecondScore");
            return new JoinedInput
            {
                FirstName = ReadString(values, nameof(JoinedInput.FirstName)),
                SecondName = ReadString(values, nameof(JoinedInput.SecondName)),
                TotalScore = firstScore + secondScore
            };
        }
    }

    private static string? ReadString(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        PluginInputMaterializerUtilities.TryGetValue(values, name, out var element)
            ? PluginInputMaterializerUtilities.ReadString(element)
            : null;

    private static int ReadInt32(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        PluginInputMaterializerUtilities.TryGetValue(values, name, out var element)
            ? PluginInputMaterializerUtilities.ReadInt32(element)
            : 0;

    private static bool ReadBoolean(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        PluginInputMaterializerUtilities.TryGetValue(values, name, out var element) && element.GetBoolean();

    private static decimal ReadDecimal(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        PluginInputMaterializerUtilities.TryGetValue(values, name, out var element)
            ? PluginInputMaterializerUtilities.ReadDecimal(element)
            : 0;

    private sealed class MemoryPluginDocumentStore<TDocument> : IPluginDocumentStore<TDocument>
        where TDocument : class
    {
        private readonly Dictionary<(string DocumentId, string PartitionKey), TDocument> _documents;

        public MemoryPluginDocumentStore(string documentId, string partitionKey, TDocument document)
            : this((documentId, partitionKey, document))
        {
        }

        public MemoryPluginDocumentStore(params (string DocumentId, string PartitionKey, TDocument Document)[] documents)
        {
            _documents = documents.ToDictionary(
                document => (document.DocumentId, document.PartitionKey),
                document => document.Document);
        }

        public Task<TDocument?> GetAsync(
            string documentId,
            string partitionKey,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                _documents.TryGetValue((documentId, partitionKey), out var document)
                    ? document
                    : null);
        }
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(InputChannelHydrationServiceTests.HydratedInput))]
[JsonSerializable(typeof(InputChannelHydrationServiceTests.SmallInput))]
[JsonSerializable(typeof(InputChannelHydrationServiceTests.WideInput))]
[JsonSerializable(typeof(InputChannelHydrationServiceTests.JoinedInput))]
[JsonSerializable(typeof(InputChannelHydrationServiceTests.DiscoveredInput))]
internal partial class InputHydrationJsonContext : JsonSerializerContext
{
}

internal sealed class DiscoveredInputMaterializerProvider : IPluginInputMaterializerProvider
{
    public static DiscoveredInputMaterializerProvider Default { get; } = new();

    public IPluginInputMaterializer<TInput>? GetMaterializer<TInput>()
        where TInput : class =>
        typeof(TInput) == typeof(InputChannelHydrationServiceTests.DiscoveredInput)
            ? (IPluginInputMaterializer<TInput>)(object)DiscoveredInputMaterializer.Default
            : null;

    private sealed class DiscoveredInputMaterializer : IPluginInputMaterializer<InputChannelHydrationServiceTests.DiscoveredInput>
    {
        public static DiscoveredInputMaterializer Default { get; } = new();

        public InputChannelHydrationServiceTests.DiscoveredInput Materialize(IReadOnlyDictionary<string, JsonElement> values) =>
            new()
            {
                Name = PluginInputMaterializerUtilities.TryGetValue(values, "Name", out var name)
                    ? PluginInputMaterializerUtilities.ReadString(name)
                    : null,
                Score = PluginInputMaterializerUtilities.TryGetValue(values, "Score", out var score)
                    ? PluginInputMaterializerUtilities.ReadInt32(score)
                    : 0
            };
    }
}

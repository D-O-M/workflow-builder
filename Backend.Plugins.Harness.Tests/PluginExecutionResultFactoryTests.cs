using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Plugins.Communication;
using Backend.Plugins.Harness;
using Shouldly;

[assembly: AssemblyMetadata("WorkflowBuilder.PluginJsonSerializerContext", "Backend.Plugins.Harness.Tests.ResultFactoryJsonContext")]

namespace Backend.Plugins.Harness.Tests;

public sealed class PluginExecutionResultFactoryTests
{
    [Test]
    public void CreateSuccess_serializes_output_with_source_generated_type_info()
    {
        var plugin = new ResultFactoryProbePlugin
        {
            Success = new SuccessOutput
            {
                Message = "done",
                Score = 42
            }
        };
        var factory = CreateFactory(
            new PluginOutputChannel<ResultFactoryProbePlugin, SuccessOutput>(
                "Success",
                p => p.Success!,
                PluginJsonSerialization.GetTypeInfo<SuccessOutput>(ResultFactoryJsonContext.Default, "output channel 'Success'")));

        var result = factory.CreateSuccess(
            plugin,
            CreateEnvelope(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            timingsMs: null,
            effectiveInputData: null);

        result.OutputData.Keys.ShouldBe(["Success"]);
        result.OutputData["Success"].GetProperty("Message").GetString().ShouldBe("done");
        result.OutputData["Success"].GetProperty("Score").GetInt32().ShouldBe(42);
    }

    [Test]
    public void CreateSuccess_omits_null_outputs()
    {
        var plugin = new ResultFactoryProbePlugin();
        var factory = CreateFactory(
            new PluginOutputChannel<ResultFactoryProbePlugin, SuccessOutput>(
                "Success",
                p => p.Success!,
                PluginJsonSerialization.GetTypeInfo<SuccessOutput>(ResultFactoryJsonContext.Default, "output channel 'Success'")));

        var result = factory.CreateSuccess(
            plugin,
            CreateEnvelope(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            timingsMs: null,
            effectiveInputData: null);

        result.OutputData.ShouldBeEmpty();
    }

    [Test]
    public void CreateSuccess_serializes_multiple_output_channels_with_exact_names()
    {
        var plugin = new ResultFactoryProbePlugin
        {
            Success = new SuccessOutput
            {
                Message = "done",
                Score = 42
            },
            Details = new DetailsOutput
            {
                Label = "second"
            }
        };
        var factory = CreateFactory(
            new PluginOutputChannel<ResultFactoryProbePlugin, SuccessOutput>(
                "Success",
                p => p.Success!,
                PluginJsonSerialization.GetTypeInfo<SuccessOutput>(ResultFactoryJsonContext.Default, "output channel 'Success'")),
            new PluginOutputChannel<ResultFactoryProbePlugin, DetailsOutput>(
                "Details",
                p => p.Details!,
                PluginJsonSerialization.GetTypeInfo<DetailsOutput>(ResultFactoryJsonContext.Default, "output channel 'Details'")));

        var result = factory.CreateSuccess(
            plugin,
            CreateEnvelope(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            timingsMs: null,
            effectiveInputData: null);

        result.OutputData.Keys.ShouldBe(["Success", "Details"], ignoreOrder: true);
        result.OutputData["Success"].GetProperty("Message").GetString().ShouldBe("done");
        result.OutputData["Details"].GetProperty("Label").GetString().ShouldBe("second");
    }

    [Test]
    public void Generated_context_metadata_is_discoverable_from_plugin_assembly()
    {
        var context = PluginJsonSerialization.ResolveContext(typeof(ResultFactoryProbePlugin));

        context.ShouldBeSameAs(ResultFactoryJsonContext.Default);
    }

    [Test]
    public void Result_factory_source_does_not_use_runtime_value_type_serialization()
    {
        var source = File.ReadAllText(FindRepoFile("Backend.Plugins.Harness", "PluginExecutionResultFactory.cs"));

        source.ShouldNotContain("value.GetType()");
        source.ShouldNotContain("GetValue(plugin)");
        source.ShouldNotContain("SerializeToElement(\r\n                value,");
    }

    [Test]
    public void Plugin_execution_result_serialization_omits_global_state()
    {
        var result = new PluginExecutionResult(
            Guid.NewGuid(),
            Succeeded: true,
            EffectiveInputData: null,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            StartedAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAt: DateTimeOffset.UtcNow,
            TimingsMs: null,
            ErrorMessage: null);

        var json = PluginExecutionResultSerializer.Serialize(result);

        json.ShouldNotContain("global" + "State");
    }

    private static PluginExecutionResultFactory<ResultFactoryProbePlugin> CreateFactory(
        params IPluginOutputChannel<ResultFactoryProbePlugin>[] outputChannels) =>
        new(outputChannels);

    private static InvocationEnvelope CreateEnvelope() =>
        new(
            ExecutableBlueprintId: 100,
            BlueprintDefinitionId: 10,
            ProcessId: Guid.NewGuid(),
            ProcessStepId: Guid.NewGuid(),
            BlueprintNodeId: Guid.NewGuid(),
            TaskDefinitionId: 7,
            TaskVersionId: 70,
            TaskVersion: "1.0.0",
            PluginName: "ResultFactoryProbe",
            InputHydrationPlan: new InputHydrationPlan
            {
                InputChannelName = "Input"
            });

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

    private sealed class ResultFactoryProbePlugin : IPlugin
    {
        public SuccessOutput? Success { get; set; }
        public DetailsOutput? Details { get; set; }
        public Task Execute() => Task.CompletedTask;
    }

    [Backend.Plugins.Harness.PluginJsonSerializable]
    public sealed class SuccessOutput
    {
        public string Message { get; set; } = string.Empty;
        public int Score { get; set; }
    }

    [Backend.Plugins.Harness.PluginJsonSerializable]
    public sealed class DetailsOutput
    {
        public string Label { get; set; } = string.Empty;
    }

}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PluginExecutionResultFactoryTests.SuccessOutput))]
[JsonSerializable(typeof(PluginExecutionResultFactoryTests.DetailsOutput))]
internal partial class ResultFactoryJsonContext : JsonSerializerContext
{
}

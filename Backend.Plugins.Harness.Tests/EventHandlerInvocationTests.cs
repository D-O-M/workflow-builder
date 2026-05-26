using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Plugins.Communication;
using Backend.Plugins.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Plugins.Harness.Tests;

public sealed class EventHandlerInvocationTests
{
    [Test]
    public async Task ProcessEnvelopeAsync_resolves_plugin_constructor_dependencies_from_invocation_scope()
    {
        var resultFactory = CreateResultFactory(
            CreateOutputChannel<ConstructorDependencyPlugin, InvocationOutput>(
                "Output",
                plugin => plugin.Output!));

        using var provider = CreateProvider(
            PluginHydrationPlan<ConstructorDependencyPlugin>.Empty,
            resultFactory,
            services => services.AddScoped<InvocationScopedDependency>());
        var handler = CreateHandler<ConstructorDependencyPlugin>(provider);

        var first = await handler.ProcessEnvelopeAsync(CreateEnvelope(), CancellationToken.None);
        var second = await handler.ProcessEnvelopeAsync(CreateEnvelope(), CancellationToken.None);

        first.Succeeded.ShouldBeTrue();
        second.Succeeded.ShouldBeTrue();
        ReadOutput(first).ScopeId.ShouldNotBe(ReadOutput(second).ScopeId);
    }

    [Test]
    public async Task ProcessEnvelopeAsync_does_not_promote_scoped_hydration_dependencies()
    {
        var resultFactory = CreateResultFactory(
            CreateOutputChannel<ScopedHydrationPlugin, InvocationOutput>(
                "Output",
                plugin => plugin.Output!));
        var hydrationPlan = new PluginHydrationPlan<ScopedHydrationPlugin>(
        [
            new ScopedHydrationDescriptor()
        ]);

        using var provider = CreateProvider(
            hydrationPlan,
            resultFactory,
            services => services.AddScoped<HydrationScopedDependency>());
        var handler = CreateHandler<ScopedHydrationPlugin>(provider);

        var first = await handler.ProcessEnvelopeAsync(CreateEnvelope(), CancellationToken.None);
        var second = await handler.ProcessEnvelopeAsync(CreateEnvelope(), CancellationToken.None);

        first.Succeeded.ShouldBeTrue();
        second.Succeeded.ShouldBeTrue();
        ReadOutput(first).ScopeId.ShouldNotBe(ReadOutput(second).ScopeId);
    }

    [Test]
    public async Task ProcessEnvelopeAsync_hydrates_input_channel()
    {
        var resultFactory = CreateResultFactory(
            CreateOutputChannel<InputHydrationPlugin, HydrationOutput>(
                "Output",
                plugin => plugin.Output!));
        var setter = new PropertySetterUtility<InputHydrationPlugin, InputDto>(plugin => plugin.Input!);
        var hydrationPlan = new PluginHydrationPlan<InputHydrationPlugin>(
        [
            new InputChannelHydrationDescriptor<InputHydrationPlugin, InputDto>("Input", setter)
        ]);

        using var provider = CreateProvider(
            hydrationPlan,
            resultFactory,
            services =>
            {
                services.AddScoped<IPluginDocumentStore<ProcessStepRecord>>(_ =>
                    new MemoryPluginDocumentStore<ProcessStepRecord>((_, _) => null));
                services.AddScoped(sp =>
                    new InputChannelHydrationService<InputDto>(
                        sp.GetRequiredService<IPluginDocumentStore<ProcessStepRecord>>(),
                        PluginJsonSerialization.RequireTypeInfo<InputDto>(
                            EventHandlerInvocationJsonContext.Default,
                            "test input channel")));
            });
        var handler = CreateHandler<InputHydrationPlugin>(provider);

        var result = await handler.ProcessEnvelopeAsync(
            CreateEnvelope(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["Text"] = Json("hydrated"),
                ["Number"] = Json(42)
            }),
            CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        var output = ReadHydrationOutput(result);
        output.Text.ShouldBe("hydrated");
        output.Number.ShouldBe(42);
        result.EffectiveInputData.ShouldNotBeNull();
        result.EffectiveInputData.Keys.ShouldBe(["Input"]);
    }

    [Test]
    public async Task ProcessEnvelopeAsync_runs_multiple_hydration_steps_in_order_during_concurrent_invocations()
    {
        ConcurrentOrderedPlugin.Reset();
        var resultFactory = CreateResultFactory(
            CreateOutputChannel<ConcurrentOrderedPlugin, OrderedOutput>(
                "Output",
                plugin => plugin.Output!));
        var hydrationPlan = new PluginHydrationPlan<ConcurrentOrderedPlugin>(
        [
            new OrderedHydrationDescriptor("first"),
            new OrderedHydrationDescriptor("second")
        ]);

        using var provider = CreateProvider(
            hydrationPlan,
            resultFactory,
            services => services.AddScoped<InvocationScopedDependency>());
        var handler = CreateHandler<ConcurrentOrderedPlugin>(provider);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => handler.ProcessEnvelopeAsync(CreateEnvelope(), CancellationToken.None)));

        foreach (var result in results)
        {
            result.Succeeded.ShouldBeTrue();
            ReadOrderedOutput(result).Steps.ShouldBe("first,second");
        }

        results
            .Select(result => ReadOrderedOutput(result).InstanceId)
            .Distinct()
            .Count()
            .ShouldBe(results.Length);
        results
            .Select(result => ReadOrderedOutput(result).ScopeId)
            .Distinct()
            .Count()
            .ShouldBe(results.Length);
    }

    [Test]
    public async Task ProcessEnvelopeAsync_returns_failure_result_when_plugin_execute_fails()
    {
        using var provider = CreateProvider(
            PluginHydrationPlan<FailingPlugin>.Empty,
            CreateResultFactory<FailingPlugin>());
        var handler = CreateHandler<FailingPlugin>(provider);

        var result = await handler.ProcessEnvelopeAsync(CreateEnvelope(), CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.OutputData.ShouldBeEmpty();
        result.ErrorMessage.ShouldBe("plugin exploded");
    }

    [Test]
    public void EventHandler_source_uses_cached_activation_and_hydration_plan()
    {
        var source = File.ReadAllText(FindRepoFile("Backend.Plugins.Harness", "EventHandler.cs"));

        source.ShouldContain("ActivatorUtilities.CreateFactory(typeof(TPlugin), Type.EmptyTypes)");
        source.ShouldNotContain("ActivatorUtilities.CreateInstance");
        source.ShouldNotContain("GetServices<IPluginHydrationStep<TPlugin>>");
    }

    private static ServiceProvider CreateProvider<TPlugin>(
        PluginHydrationPlan<TPlugin> hydrationPlan,
        PluginExecutionResultFactory<TPlugin> resultFactory,
        Action<IServiceCollection>? configureServices = null)
        where TPlugin : class, IPlugin
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(hydrationPlan);
        services.AddSingleton(resultFactory);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static Backend.Plugins.Harness.EventHandler<TPlugin> CreateHandler<TPlugin>(
        ServiceProvider provider)
        where TPlugin : class, IPlugin =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<PluginHydrationPlan<TPlugin>>(),
            provider.GetRequiredService<PluginExecutionResultFactory<TPlugin>>(),
            NullLogger<Backend.Plugins.Harness.EventHandler<TPlugin>>.Instance);

    private static PluginExecutionResultFactory<TPlugin> CreateResultFactory<TPlugin>(
        params IPluginOutputChannel<TPlugin>[] outputChannels)
        where TPlugin : class, IPlugin =>
        new(outputChannels);

    private static IPluginOutputChannel<TPlugin> CreateOutputChannel<TPlugin, TOutput>(
        string name,
        Func<TPlugin, TOutput> getter)
        where TPlugin : class, IPlugin
        where TOutput : class =>
        new PluginOutputChannel<TPlugin, TOutput>(
            name,
            getter,
            PluginJsonSerialization.GetTypeInfo<TOutput>(
                EventHandlerInvocationJsonContext.Default,
                $"output channel '{name}'"));

    private static InvocationOutput ReadOutput(PluginExecutionResult result) =>
        result.OutputData["Output"].Deserialize<InvocationOutput>(JsonSerializerOptions.Web)!;

    private static HydrationOutput ReadHydrationOutput(PluginExecutionResult result) =>
        result.OutputData["Output"].Deserialize<HydrationOutput>(JsonSerializerOptions.Web)!;

    private static OrderedOutput ReadOrderedOutput(PluginExecutionResult result) =>
        result.OutputData["Output"].Deserialize<OrderedOutput>(JsonSerializerOptions.Web)!;

    private static InvocationEnvelope CreateEnvelope(
        Dictionary<string, JsonElement>? initialInput = null,
        Guid? processId = null,
        int executableBlueprintId = 100) =>
        new(
            ExecutableBlueprintId: executableBlueprintId,
            BlueprintDefinitionId: 10,
            ProcessId: processId ?? Guid.NewGuid(),
            ProcessStepId: Guid.NewGuid(),
            BlueprintNodeId: Guid.NewGuid(),
            TaskDefinitionId: 7,
            TaskVersionId: 70,
            TaskVersion: "1.0.0",
            PluginName: "InvocationProbe",
            InputHydrationPlan: new InputHydrationPlan
            {
                InputChannelName = "Input",
                InitialInput = initialInput ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            });

    private static JsonElement Json<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

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

    private sealed class ConstructorDependencyPlugin : IPlugin
    {
        private readonly InvocationScopedDependency _dependency;

        public ConstructorDependencyPlugin(InvocationScopedDependency dependency)
        {
            _dependency = dependency;
        }

        public InvocationOutput? Output { get; private set; }

        public Task Execute()
        {
            Output = new InvocationOutput
            {
                ScopeId = _dependency.Id
            };
            return Task.CompletedTask;
        }
    }

    private sealed class ScopedHydrationPlugin : IPlugin
    {
        public Guid HydrationScopeId { get; set; }
        public InvocationOutput? Output { get; private set; }

        public Task Execute()
        {
            Output = new InvocationOutput
            {
                ScopeId = HydrationScopeId
            };
            return Task.CompletedTask;
        }
    }

    private sealed class InputHydrationPlugin : IPlugin
    {
        public InputDto? Input { get; set; }
        public HydrationOutput? Output { get; private set; }

        public Task Execute()
        {
            Output = new HydrationOutput
            {
                Text = Input?.Text,
                Number = Input?.Number ?? 0
            };
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrentOrderedPlugin : IPlugin
    {
        private static int s_nextInstanceId;
        private readonly InvocationScopedDependency _dependency;
        private readonly List<string> _steps = [];

        public ConcurrentOrderedPlugin(InvocationScopedDependency dependency)
        {
            _dependency = dependency;
            InstanceId = Interlocked.Increment(ref s_nextInstanceId);
        }

        public int InstanceId { get; }
        public OrderedOutput? Output { get; private set; }

        public static void Reset() => s_nextInstanceId = 0;

        public void AddStep(string step) => _steps.Add(step);

        public Task Execute()
        {
            Output = new OrderedOutput
            {
                InstanceId = InstanceId,
                ScopeId = _dependency.Id,
                Steps = string.Join(',', _steps)
            };
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPlugin : IPlugin
    {
        public Task Execute() => throw new InvalidOperationException("plugin exploded");
    }

    private sealed class ScopedHydrationDescriptor : IPluginHydrationStepDescriptor<ScopedHydrationPlugin>
    {
        public Task HydrateAsync(
            IServiceProvider services,
            ScopedHydrationPlugin plugin,
            InvocationEnvelope envelope,
            PluginHydrationContext context,
            CancellationToken cancellationToken)
        {
            plugin.HydrationScopeId = services.GetRequiredService<HydrationScopedDependency>().Id;
            return Task.CompletedTask;
        }
    }

    private sealed class OrderedHydrationDescriptor : IPluginHydrationStepDescriptor<ConcurrentOrderedPlugin>
    {
        private readonly string _step;

        public OrderedHydrationDescriptor(string step)
        {
            _step = step;
        }

        public Task HydrateAsync(
            IServiceProvider services,
            ConcurrentOrderedPlugin plugin,
            InvocationEnvelope envelope,
            PluginHydrationContext context,
            CancellationToken cancellationToken)
        {
            plugin.AddStep(_step);
            return Task.CompletedTask;
        }
    }

    private sealed class InvocationScopedDependency
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class HydrationScopedDependency
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class MemoryPluginDocumentStore<TDocument> : IPluginDocumentStore<TDocument>
        where TDocument : class
    {
        private readonly Func<string, string, TDocument?> _resolver;

        public MemoryPluginDocumentStore(Func<string, string, TDocument?> resolver)
        {
            _resolver = resolver;
        }

        public Task<TDocument?> GetAsync(
            string documentId,
            string partitionKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(_resolver(documentId, partitionKey));
    }

    [PluginJsonSerializable]
    public sealed class InputDto
    {
        public string? Text { get; set; }
        public int Number { get; set; }
    }

    [PluginJsonSerializable]
    public sealed class InvocationOutput
    {
        public Guid ScopeId { get; set; }
    }

    [PluginJsonSerializable]
    public sealed class HydrationOutput
    {
        public string? Text { get; set; }
        public int Number { get; set; }
    }

    [PluginJsonSerializable]
    public sealed class OrderedOutput
    {
        public int InstanceId { get; set; }
        public Guid ScopeId { get; set; }
        public string Steps { get; set; } = string.Empty;
    }

}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(EventHandlerInvocationTests.InputDto))]
[JsonSerializable(typeof(EventHandlerInvocationTests.InvocationOutput))]
[JsonSerializable(typeof(EventHandlerInvocationTests.HydrationOutput))]
[JsonSerializable(typeof(EventHandlerInvocationTests.OrderedOutput))]
internal partial class EventHandlerInvocationJsonContext : JsonSerializerContext
{
}

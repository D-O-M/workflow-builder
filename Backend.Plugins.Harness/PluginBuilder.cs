using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Plugins.Communication;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Backend.Plugins.Harness;

public class PluginBuilder<TPlugin>
    where TPlugin : class, IPlugin
{
    private readonly string _displayName;
    private readonly Dictionary<string, PluginChannel> _inputChannels = new();
    private readonly Dictionary<string, PluginChannel> _outputChannels = new();
    private readonly HostApplicationBuilder _hostAppBuilder;
    private IHost? host = null;

    private Type? _globalStateType;

    public IServiceCollection Services => _hostAppBuilder.Services;

    public PluginBuilder(string displayName)
    {
        _hostAppBuilder = Host.CreateApplicationBuilder(Environment.GetCommandLineArgs());

        RegisterCosmos();

        // Register the event loop
        _hostAppBuilder.Services.AddHostedService<EventHandler<TPlugin>>();

        _hostAppBuilder.Logging.ClearProviders();

        _hostAppBuilder.Configuration.AddEnvironmentVariables();

        _hostAppBuilder.Services.AddOpenTelemetry()
            .ConfigureResource(builder => builder.AddService(displayName))
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(displayName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
            })
            .WithLogging()
            .UseOtlpExporter();

        _displayName = displayName;
    }

    public PluginBuilder<TPlugin> ConfigureInputChannel<TChannelType>(
        Expression<Func<TPlugin, TChannelType>> propertySelector)
        where TChannelType : class
    {
        var name = GetPropertyInfo(propertySelector).Name;

        if (_inputChannels.ContainsKey(name))
            throw new InvalidOperationException($"Input channel '{name}' is already configured.");

        _inputChannels.Add(name,
            new PluginChannel<TPlugin, TChannelType>(
                name, propertySelector.Compile()));

        // Register setter utility
        _hostAppBuilder.Services.AddSingleton(
            new PropertySetterUtility<TPlugin, TChannelType>(propertySelector));

        // Register hydrator for TaskObject -> TChannelType
        _hostAppBuilder.Services.AddSingleton(sp =>
        {
            Func<TaskObject, string, TChannelType> valueExtractionFunction =
                (task, channelName) =>
                {
                    if (string.IsNullOrWhiteSpace(channelName) || task.OutputData is null)
                        throw new InvalidOperationException("Channel name or output data missing.");

                    if (!task.OutputData.TryGetValue(channelName, out var json))
                        throw new InvalidOperationException($"OutputData does not contain key '{channelName}'.");

                    return JsonSerializer.Deserialize<TChannelType>(
                        json, JsonSerializerOptions.Web)!;
                };

            var cosmosRepo = sp.GetRequiredService<CosmosRepository<TaskObject>>();

            return new PluginPropertyHydrationService<TaskObject, TChannelType>(
                cosmosRepo, valueExtractionFunction);
        });

        // Register the step façade (Option B: multiple steps)
        _hostAppBuilder.Services.AddSingleton<IPluginHydrationStep<TPlugin>>(sp =>
        {
            var hydrator = sp.GetRequiredService<PluginPropertyHydrationService<TaskObject, TChannelType>>();
            var setter = sp.GetRequiredService<PropertySetterUtility<TPlugin, TChannelType>>();

            // These selectors are based on your commented usage in EventHandler.
            // Adjust if your InvocationEnvelope uses different fields.
            Func<InvocationEnvelope, Guid> documentIdSelector = env => env.WorkflowTaskInstanceId;
            Func<InvocationEnvelope, Guid> partitionKeySelector = env => env.WorkflowInstanceId;

            return new PluginHydrationStep<TPlugin, TaskObject, TChannelType>(
                channelName: name,
                hydrator: hydrator,
                setter: setter,
                documentIdSelector: documentIdSelector,
                partitionKeySelector: partitionKeySelector);
        });

        return this;
    }

    public PluginBuilder<TPlugin> ConfigureOutputChannel<TChannelType>(
        Expression<Func<TPlugin, TChannelType>> propertySelector)
        where TChannelType : class
    {
        var property = GetPropertyInfo(propertySelector);
        var name = property.Name;
        var getter = propertySelector.Compile();

        if (_outputChannels.ContainsKey(name))
            throw new InvalidOperationException($"Output channel '{name}' is already configured.");

        _outputChannels.Add(name, new PluginChannel<TPlugin, TChannelType>(name, getter));
        return this;
    }

    public PluginBuilder<TPlugin> WithGlobalState<TGlobalState>(
        Expression<Func<TPlugin, TGlobalState>> propertySelector)
        where TGlobalState : class, IGlobalState
    {
        var name = GetPropertyInfo(propertySelector).Name;

        if (_inputChannels.ContainsKey(name))
            throw new InvalidOperationException($"Input channel '{name}' is already configured.");

        _inputChannels.Add(name,
            new PluginChannel<TPlugin, TGlobalState>(
                name, propertySelector.Compile()));

        _globalStateType = typeof(TGlobalState);

        _hostAppBuilder.Services.AddSingleton(
            new PropertySetterUtility<TPlugin, TGlobalState>(propertySelector));

        _hostAppBuilder.Services.AddSingleton(sp =>
        {
            var cosmosRepo = sp.GetRequiredService<CosmosRepository<WorkflowObject<TGlobalState>>>();

            return new PluginPropertyHydrationService<WorkflowObject<TGlobalState>, TGlobalState>(
                cosmosRepo,
                (workflow, _) => workflow.GlobalState);
        });

        // Global state step: always run (no channel gating)
        _hostAppBuilder.Services.AddSingleton<IPluginHydrationStep<TPlugin>>(sp =>
        {
            var hydrator = sp.GetRequiredService<PluginPropertyHydrationService<WorkflowObject<TGlobalState>, TGlobalState>>();
            var setter = sp.GetRequiredService<PropertySetterUtility<TPlugin, TGlobalState>>();

            // Adjust these if your WorkflowObject is keyed differently.
            Func<InvocationEnvelope, Guid> documentIdSelector = env => env.WorkflowInstanceId;
            Func<InvocationEnvelope, Guid> partitionKeySelector = env => env.WorkflowBlueprintId;

            return new GlobalStateHydrationStep<TPlugin, WorkflowObject<TGlobalState>, TGlobalState>(
                hydrator, setter, documentIdSelector, partitionKeySelector);
        });

        _hostAppBuilder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new CosmosRepository<WorkflowObject<TGlobalState>>(
                client.GetContainer("enginedb", "workflows"),
                task => task.WorkflowInstanceId,
                task => new PartitionKey(task.BlueprintId));
        });

        return this;
    }

    public Plugin<TPlugin> Build()
    {
        string? manifestPath = null;
        string? schemaDir = null;
        string? aggregated = null;
        int schemaCount = 0;
        Exception? pendingError = null;

#if DEBUG
        try
        {
            var projectDir = TryFindProjectDirectory(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

            var manifest = ManifestGenerator.CreateManifest(
                displayName: _displayName,
                inputChannel: _inputChannels.Values.Select(c => new KeyValuePair<string, Type>(c.Name, c.MessageType)).FirstOrDefault(),
                outputChannels: _outputChannels.Values.Select(c => new KeyValuePair<string, Type>(c.Name, c.MessageType)),
                globalStateType: _globalStateType);

            (manifestPath, schemaDir, aggregated, schemaCount) =
                ManifestGenerator.EmitAll(
                    projectDir: projectDir,
                    manifest: manifest,
                    rootTypes: EnumerateRootTypes());
        }
        catch (Exception ex)
        {
            pendingError = ex;
        }
#endif

        host = _hostAppBuilder.Build();

#if DEBUG
        var logger = host.Services.GetService<ILogger<PluginBuilder<TPlugin>>>();
        if (pendingError is not null)
        {
            logger?.LogWarning(pendingError, "Failed to generate manifest/schemas (Debug-only).");
        }
        else
        {
            logger?.LogInformation(
                "Generated manifest at {Manifest} and {Count} schema file(s) under {SchemaDir}. Aggregated: {Agg}",
                manifestPath, schemaCount, schemaDir, aggregated ?? "(none)");
        }
#endif

        return new Plugin<TPlugin>(this);
    }

    internal async Task RunAsync()
    {
        host ??= _hostAppBuilder.Build();
        await host.RunAsync();
    }

    private IEnumerable<Type> EnumerateRootTypes()
    {
        foreach (var c in _inputChannels.Values) yield return c.MessageType;
        foreach (var c in _outputChannels.Values) yield return c.MessageType;
        if (_globalStateType is not null) yield return _globalStateType;
    }

    private static PropertyInfo GetPropertyInfo<TChannelType>(
        Expression<Func<TPlugin, TChannelType>> expr)
    {
        if (expr.Body is not MemberExpression m || m.Member is not PropertyInfo pi)
            throw new ArgumentException("Selector must be p => p.Property");
        return pi;
    }

    private static string? TryFindProjectDirectory(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).Any())
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private void RegisterCosmos()
    {
        var builder = _hostAppBuilder;

        var cosmosConnectionString =
            "AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

        var databaseName = "enginedb";
        var containerName = "tasks";

        builder.Services.AddSingleton(sp =>
        {
            var options = new CosmosClientOptions
            {
                ApplicationName = "MyApp",
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(10),
                UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                }
            };

            return new CosmosClient(cosmosConnectionString, options);
        });

        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new CosmosRepository<TaskObject>(
                client.GetContainer(databaseName, containerName),
                task => task.Id,
                task => new PartitionKey(task.WorkflowInstanceId));
        });
    }

    /// <summary>
    /// Global state hydration step that always runs.
    /// </summary>
    private sealed class GlobalStateHydrationStep<TP, TDoc, TProp> : IPluginHydrationStep<TP>
        where TP : class, IPlugin
        where TDoc : class
    {
        private readonly PluginPropertyHydrationService<TDoc, TProp> _hydrator;
        private readonly PropertySetterUtility<TP, TProp> _setter;
        private readonly Func<InvocationEnvelope, Guid> _documentIdSelector;
        private readonly Func<InvocationEnvelope, Guid> _partitionKeySelector;

        public GlobalStateHydrationStep(
            PluginPropertyHydrationService<TDoc, TProp> hydrator,
            PropertySetterUtility<TP, TProp> setter,
            Func<InvocationEnvelope, Guid> documentIdSelector,
            Func<InvocationEnvelope, Guid> partitionKeySelector)
        {
            _hydrator = hydrator;
            _setter = setter;
            _documentIdSelector = documentIdSelector;
            _partitionKeySelector = partitionKeySelector;
        }

        public bool CanHydrate(InvocationEnvelope envelope) => true;

        public Task HydrateAsync(TP plugin, InvocationEnvelope envelope, CancellationToken cancellationToken)
        {
            var documentId = _documentIdSelector(envelope);
            var partitionKey = _partitionKeySelector(envelope);

            // outputChannelName is ignored by the global state extractor; pass empty.
            return _hydrator.HydratePluginProperty(
                value => _setter.Set(plugin, value),
                documentId,
                partitionKey,
                outputChannelName: string.Empty,
                cancellationToken);
        }
    }
}

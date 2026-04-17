using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Repositories;
using Backend.Plugins.Communication;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    private Func<TPlugin, object?>? _globalStateGetter;
    private readonly Dictionary<string, PluginChannel> _inputChannels = new();
    private readonly Dictionary<string, PluginChannel> _outputChannels = new();
    private readonly HostApplicationBuilder _hostAppBuilder;
    private IHost? host = null;

    private Type? _globalStateType;

    public IServiceCollection Services => _hostAppBuilder.Services;

    public PluginBuilder(string displayName)
    {
        _hostAppBuilder = Host.CreateApplicationBuilder(Environment.GetCommandLineArgs());
        _hostAppBuilder.Configuration.AddEnvironmentVariables();

        // Register the event loop
        _hostAppBuilder.Services.AddHostedService<EventHandler<TPlugin>>();

        _hostAppBuilder.Logging.ClearProviders();

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

        EnsureTaskDocumentStoreRegistered();

        _hostAppBuilder.Services.TryAddSingleton(sp =>
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

            return new PluginPropertyHydrationService<TaskObject, TChannelType>(
                sp.GetRequiredService<IPluginDocumentStore<TaskObject>>(),
                valueExtractionFunction);
        });

        _hostAppBuilder.Services.AddSingleton<IPluginHydrationStep<TPlugin>>(sp =>
        {
            var hydrator = sp.GetRequiredService<PluginPropertyHydrationService<TaskObject, TChannelType>>();
            var setter = sp.GetRequiredService<PropertySetterUtility<TPlugin, TChannelType>>();
            Func<InvocationEnvelope, string> documentIdSelector =
                env => env.PreviousTaskDetails.PreviousTaskInstanceId.ToString();
            Func<InvocationEnvelope, string> partitionKeySelector =
                env => env.WorkflowInstanceId.ToString();

            return new PluginHydrationStep<TPlugin, TaskObject, TChannelType>(
                name,
                hydrator,
                setter,
                documentIdSelector,
                partitionKeySelector);
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
        var globalStateGetter = propertySelector.Compile();
        _globalStateGetter = plugin => globalStateGetter(plugin);

        _hostAppBuilder.Services.AddSingleton(
            new PropertySetterUtility<TPlugin, TGlobalState>(propertySelector));

        EnsureWorkflowDocumentStoreRegistered<TGlobalState>();

        _hostAppBuilder.Services.TryAddSingleton(sp =>
            new PluginPropertyHydrationService<WorkflowObject<TGlobalState>, TGlobalState>(
                sp.GetRequiredService<IPluginDocumentStore<WorkflowObject<TGlobalState>>>(),
                (workflow, _) => workflow.GlobalState));

        _hostAppBuilder.Services.AddSingleton<IPluginHydrationStep<TPlugin>>(sp =>
        {
            var hydrator = sp.GetRequiredService<PluginPropertyHydrationService<WorkflowObject<TGlobalState>, TGlobalState>>();
            var setter = sp.GetRequiredService<PropertySetterUtility<TPlugin, TGlobalState>>();
            Func<InvocationEnvelope, string> documentIdSelector =
                env => env.WorkflowInstanceId.ToString();
            Func<InvocationEnvelope, string> partitionKeySelector =
                env => env.WorkflowBlueprintId.ToString();

            return new GlobalStateHydrationStep<TPlugin, WorkflowObject<TGlobalState>, TGlobalState>(
                hydrator,
                setter,
                documentIdSelector,
                partitionKeySelector);
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

        if (_globalStateType is null)
        {
            WithGlobalState(p => p.GlobalState);
        }

        _hostAppBuilder.Services.AddSingleton(
            new PluginExecutionResultFactory<TPlugin>(
                _outputChannels.Values.ToList(),
                _globalStateGetter ?? (_ => new GlobalStateStub())));

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

    private bool UseFileStateStore() =>
        string.Equals(
            _hostAppBuilder.Configuration["PluginStateStore:Mode"],
            "File",
            StringComparison.OrdinalIgnoreCase);

    private string GetFileStateRootPath() =>
        _hostAppBuilder.Configuration["PluginStateStore:RootPath"]
        ?? throw new InvalidOperationException(
            "Plugin state store root path is not configured for file-based hydration.");

    private string GetCosmosDatabaseName() =>
        _hostAppBuilder.Configuration["AzureCosmos:DatabaseName"]
        ?? "enginedb";

    private string GetCosmosConnectionString() =>
        _hostAppBuilder.Configuration.GetConnectionString(
            _hostAppBuilder.Configuration["AzureCosmos:ConnectionName"]
            ?? "enginedb")
        ?? _hostAppBuilder.Configuration.GetConnectionString("enginedb")
        ?? throw new InvalidOperationException(
            "Cosmos DB connection string is not configured for the plugin harness.");

    private void EnsureCosmosClientRegistered()
    {
        if (UseFileStateStore())
        {
            return;
        }

        _hostAppBuilder.Services.TryAddSingleton(sp =>
        {
            return new CosmosClient(
                GetCosmosConnectionString(),
                new CosmosClientOptions
                {
                    ApplicationName = "WorkflowBuilder.PluginHarness",
                    ConnectionMode = ConnectionMode.Gateway,
                    RequestTimeout = TimeSpan.FromSeconds(10),
                    UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        PropertyNameCaseInsensitive = true
                    }
                });
        });
    }

    private void EnsureTaskDocumentStoreRegistered()
    {
        if (UseFileStateStore())
        {
            _hostAppBuilder.Services.TryAddSingleton<IPluginDocumentStore<TaskObject>>(
                _ => new FilePluginDocumentStore<TaskObject>(GetFileStateRootPath(), "tasks"));
            return;
        }

        EnsureCosmosClientRegistered();
        var databaseName = GetCosmosDatabaseName();
        _hostAppBuilder.Services.TryAddSingleton(sp =>
            new CosmosRepository<TaskObject>(
                sp.GetRequiredService<CosmosClient>().GetContainer(databaseName, "tasks"),
                task => task.Id,
                task => new PartitionKey(task.WorkflowInstanceId)));
        _hostAppBuilder.Services.TryAddSingleton<IPluginDocumentStore<TaskObject>>(sp =>
            new CosmosPluginDocumentStore<TaskObject>(
                sp.GetRequiredService<CosmosRepository<TaskObject>>()));
    }

    private void EnsureWorkflowDocumentStoreRegistered<TGlobalState>()
        where TGlobalState : class
    {
        if (UseFileStateStore())
        {
            _hostAppBuilder.Services.TryAddSingleton<IPluginDocumentStore<WorkflowObject<TGlobalState>>>(
                _ => new FilePluginDocumentStore<WorkflowObject<TGlobalState>>(GetFileStateRootPath(), "workflows"));
            return;
        }

        EnsureCosmosClientRegistered();
        var databaseName = GetCosmosDatabaseName();
        _hostAppBuilder.Services.TryAddSingleton(sp =>
            new CosmosRepository<WorkflowObject<TGlobalState>>(
                sp.GetRequiredService<CosmosClient>().GetContainer(databaseName, "workflows"),
                workflow => workflow.WorkflowInstanceId,
                workflow => new PartitionKey(workflow.BlueprintId)));
        _hostAppBuilder.Services.TryAddSingleton<IPluginDocumentStore<WorkflowObject<TGlobalState>>>(sp =>
            new CosmosPluginDocumentStore<WorkflowObject<TGlobalState>>(
                sp.GetRequiredService<CosmosRepository<WorkflowObject<TGlobalState>>>()));
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
        private readonly Func<InvocationEnvelope, string> _documentIdSelector;
        private readonly Func<InvocationEnvelope, string> _partitionKeySelector;

        public GlobalStateHydrationStep(
            PluginPropertyHydrationService<TDoc, TProp> hydrator,
            PropertySetterUtility<TP, TProp> setter,
            Func<InvocationEnvelope, string> documentIdSelector,
            Func<InvocationEnvelope, string> partitionKeySelector)
        {
            _hydrator = hydrator;
            _setter = setter;
            _documentIdSelector = documentIdSelector;
            _partitionKeySelector = partitionKeySelector;
        }

        public Task HydrateAsync(TP plugin, InvocationEnvelope envelope, CancellationToken cancellationToken)
            => _hydrator.HydratePluginProperty(
                value => _setter.Set(plugin, value),
                _documentIdSelector(envelope),
                _partitionKeySelector(envelope),
                outputChannelName: string.Empty,
                cancellationToken);
    }
}

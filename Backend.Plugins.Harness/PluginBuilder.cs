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
    private const string ProcessStepsContainerName = "processsteps";
    private const string ProcessesContainerName = "processes";
    private readonly string _displayName;
    private Func<TPlugin, object?>? _globalStateGetter;
    private readonly Dictionary<string, PluginChannel> _inputChannels = new();
    private readonly Dictionary<string, PluginChannel> _outputChannels = new();
    private readonly HostApplicationBuilder _hostAppBuilder;
    private IHost? host = null;

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
                    .AddSource(PluginExecutionTelemetry.ActivitySourceName)
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
            Func<ProcessStepRecord, string, TChannelType> valueExtractionFunction =
                (task, channelName) =>
                {
                    if (string.IsNullOrWhiteSpace(channelName) || task.InputData is null)
                        throw new InvalidOperationException("Channel name or output data missing.");

                    if (!task.InputData.TryGetValue(channelName, out var json))
                        throw new InvalidOperationException($"InputData does not contain key '{channelName}'.");

                    return JsonSerializer.Deserialize<TChannelType>(
                        json, JsonSerializerOptions.Web)!;
                };

            return new PluginPropertyHydrationService<ProcessStepRecord, TChannelType>(
                sp.GetRequiredService<IPluginDocumentStore<ProcessStepRecord>>(),
                valueExtractionFunction);
        });

        _hostAppBuilder.Services.AddSingleton<IPluginHydrationStep<TPlugin>>(sp =>
        {
            var hydrator = sp.GetRequiredService<PluginPropertyHydrationService<ProcessStepRecord, TChannelType>>();
            var setter = sp.GetRequiredService<PropertySetterUtility<TPlugin, TChannelType>>();
            Func<InvocationEnvelope, string> documentIdSelector =
                env => env.ProcessStepId.ToString();
            Func<InvocationEnvelope, string> partitionKeySelector =
                env => env.ProcessId.ToString();

            return new PluginHydrationStep<TPlugin, ProcessStepRecord, TChannelType>(
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

        var globalStateGetter = propertySelector.Compile();
        _globalStateGetter = plugin => globalStateGetter(plugin);

        _hostAppBuilder.Services.AddSingleton(
            new PropertySetterUtility<TPlugin, TGlobalState>(propertySelector));

        EnsureWorkflowDocumentStoreRegistered<TGlobalState>();

        _hostAppBuilder.Services.TryAddSingleton(sp =>
            new PluginPropertyHydrationService<ProcessInstanceRecord<TGlobalState>, TGlobalState>(
                sp.GetRequiredService<IPluginDocumentStore<ProcessInstanceRecord<TGlobalState>>>(),
                (workflow, _) => workflow.GlobalState));

        _hostAppBuilder.Services.AddSingleton<IPluginHydrationStep<TPlugin>>(sp =>
        {
            var hydrator = sp.GetRequiredService<PluginPropertyHydrationService<ProcessInstanceRecord<TGlobalState>, TGlobalState>>();
            var setter = sp.GetRequiredService<PropertySetterUtility<TPlugin, TGlobalState>>();
            Func<InvocationEnvelope, string> documentIdSelector =
                env => env.ProcessId.ToString();
            Func<InvocationEnvelope, string> partitionKeySelector =
                env => env.ExecutableBlueprintId.ToString();

            return new GlobalStateHydrationStep<TPlugin, ProcessInstanceRecord<TGlobalState>, TGlobalState>(
                hydrator,
                setter,
                documentIdSelector,
                partitionKeySelector);
        });

        return this;
    }

    public Plugin<TPlugin> Build()
    {
        _hostAppBuilder.Services.AddSingleton(
            new PluginExecutionResultFactory<TPlugin>(
                _outputChannels.Values.ToList(),
                _globalStateGetter));

        host = _hostAppBuilder.Build();

        return new Plugin<TPlugin>(this);
    }

    internal async Task RunAsync()
    {
        host ??= _hostAppBuilder.Build();

        await host.RunAsync();
    }

    private static PropertyInfo GetPropertyInfo<TChannelType>(
        Expression<Func<TPlugin, TChannelType>> expr)
    {
        if (expr.Body is not MemberExpression m || m.Member is not PropertyInfo pi)
            throw new ArgumentException("Selector must be p => p.Property");
        return pi;
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
        _hostAppBuilder.Configuration["AzureCosmos:ConnectionString"]
        ?? _hostAppBuilder.Configuration.GetConnectionString(
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
                    UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateJsonOptions()
                });
        });
    }

    private void EnsureTaskDocumentStoreRegistered()
    {
        if (UseFileStateStore())
        {
            _hostAppBuilder.Services.TryAddSingleton<IPluginDocumentStore<ProcessStepRecord>>(
                _ => new FilePluginDocumentStore<ProcessStepRecord>(GetFileStateRootPath(), ProcessStepsContainerName));
            return;
        }

        EnsureCosmosClientRegistered();
        var databaseName = GetCosmosDatabaseName();
        _hostAppBuilder.Services.TryAddSingleton(sp =>
            new CosmosRepository<ProcessStepRecord>(
                sp.GetRequiredService<CosmosClient>().GetContainer(databaseName, ProcessStepsContainerName),
                task => task.Id,
                task => new PartitionKey(task.ProcessId)));
        _hostAppBuilder.Services.TryAddSingleton<IPluginDocumentStore<ProcessStepRecord>>(sp =>
            new CosmosPluginDocumentStore<ProcessStepRecord>(
                sp.GetRequiredService<CosmosRepository<ProcessStepRecord>>()));
    }

    private void EnsureWorkflowDocumentStoreRegistered<TGlobalState>()
        where TGlobalState : class
    {
        if (UseFileStateStore())
        {
            _hostAppBuilder.Services.TryAddSingleton<IPluginDocumentStore<ProcessInstanceRecord<TGlobalState>>>(
                _ => new FilePluginDocumentStore<ProcessInstanceRecord<TGlobalState>>(GetFileStateRootPath(), ProcessesContainerName));
            return;
        }

        EnsureCosmosClientRegistered();
        var databaseName = GetCosmosDatabaseName();
        _hostAppBuilder.Services.TryAddSingleton(sp =>
            new CosmosRepository<ProcessInstanceRecord<TGlobalState>>(
                sp.GetRequiredService<CosmosClient>().GetContainer(databaseName, ProcessesContainerName),
                workflow => workflow.ProcessId,
                workflow => new PartitionKey(workflow.BlueprintId)));
        _hostAppBuilder.Services.TryAddSingleton<IPluginDocumentStore<ProcessInstanceRecord<TGlobalState>>>(sp =>
            new CosmosPluginDocumentStore<ProcessInstanceRecord<TGlobalState>>(
                sp.GetRequiredService<CosmosRepository<ProcessInstanceRecord<TGlobalState>>>()));
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

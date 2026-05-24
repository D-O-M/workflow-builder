using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using Backend.Engine.Capabilities;
using Backend.Database.State.Objects;
using Backend.Plugins.Configuration;
using Backend.Plugins.Communication;
using Backend.Topology;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Backend.Plugins.Harness;

public class PluginBuilder<TPlugin>
    where TPlugin : class, IPlugin
{
    private static readonly string ProcessStepsContainerName = WorkflowBuilderTopology.Cosmos.Containers.ProcessSteps.Name;
    private static readonly string ProcessesContainerName = WorkflowBuilderTopology.Cosmos.Containers.Processes.Name;
    private readonly string _displayName;
    private readonly JsonSerializerContext? _jsonSerializerContext;
    private IPluginGlobalStateSerializer<TPlugin>? _globalStateSerializer;
    private readonly Dictionary<string, PluginChannel> _inputChannels = new();
    private readonly Dictionary<string, IPluginOutputChannel<TPlugin>> _outputChannels = new();
    private readonly List<IPluginHydrationStepDescriptor<TPlugin>> _hydrationStepDescriptors = [];
    private readonly HashSet<Type> _capabilityTypes = [];
    private readonly HashSet<Type> _configurationTypes = [];
    private readonly HostApplicationBuilder _hostAppBuilder;
    private IHost? host = null;

    public IServiceCollection Services => _hostAppBuilder.Services;

    public PluginBuilder(string displayName)
    {
        _hostAppBuilder = Host.CreateApplicationBuilder(Environment.GetCommandLineArgs());
        _hostAppBuilder.Configuration.AddEnvironmentVariables();
        AddConfigFiles(_hostAppBuilder.Configuration, Environment.GetCommandLineArgs());

        // Register the event loop
        _hostAppBuilder.Services.AddHostedService<EventHandler<TPlugin>>();

        _hostAppBuilder.Logging.ClearProviders();
        _jsonSerializerContext = PluginJsonSerialization.ResolveContext(typeof(TPlugin));

        var openTelemetry = _hostAppBuilder.Services.AddOpenTelemetry()
            .ConfigureResource(builder => builder.AddService(displayName))
            .WithMetrics(metrics =>
            {
                metrics.AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(displayName)
                    .AddSource(PluginExecutionTelemetry.ActivitySourceName)
                    .AddHttpClientInstrumentation();
            })
            .WithLogging();

        _hostAppBuilder.Services.Configure<OpenTelemetryLoggerOptions>(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        if (UseOtlpExporter())
        {
            openTelemetry.UseOtlpExporter();
        }

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
            new PluginChannel(name, typeof(TChannelType)));

        var setter = new PropertySetterUtility<TPlugin, TChannelType>(propertySelector);
        _hostAppBuilder.Services.AddSingleton(setter);

        EnsureTaskDocumentStoreRegistered();

        _hostAppBuilder.Services.TryAddSingleton(sp =>
            new InputChannelHydrationService<TChannelType>(
                sp.GetRequiredService<IPluginDocumentStore<ProcessStepRecord>>()));

        _hydrationStepDescriptors.Add(
            new InputChannelHydrationDescriptor<TPlugin, TChannelType>(
                name,
                setter));

        return this;
    }

    public PluginBuilder<TPlugin> RequireCapability<TCapability>()
        where TCapability : class
    {
        var capabilityType = typeof(TCapability);
        if (!CapabilityDescriptorReader.IsApprovedCapabilityType(capabilityType))
        {
            throw new InvalidOperationException(
                $"Plugin capability type '{capabilityType.FullName}' must live under '{CapabilityDescriptorReader.CapabilityNamespacePrefix}'.");
        }

        if (!_capabilityTypes.Add(capabilityType))
        {
            return this;
        }

        _ = CapabilityDescriptorReader.Describe(capabilityType);
        foreach (var optionsType in CapabilityDescriptorReader.GetConfigurationTypes(capabilityType))
        {
            RegisterConfigurationType(optionsType);
        }

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

        _outputChannels.Add(
            name,
            new PluginOutputChannel<TPlugin, TChannelType>(
                name,
                getter,
                PluginJsonSerialization.GetTypeInfo<TChannelType>(
                    _jsonSerializerContext,
                    $"output channel '{name}'")));
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
            new PluginChannel(name, typeof(TGlobalState)));

        var globalStateGetter = propertySelector.Compile();
        _globalStateSerializer = new PluginGlobalStateSerializer<TPlugin, TGlobalState>(
            globalStateGetter,
            PluginJsonSerialization.GetTypeInfo<TGlobalState>(
                _jsonSerializerContext,
                "global state"));

        var setter = new PropertySetterUtility<TPlugin, TGlobalState>(propertySelector);
        _hostAppBuilder.Services.AddSingleton(setter);

        EnsureWorkflowDocumentStoreRegistered<TGlobalState>();

        _hostAppBuilder.Services.TryAddSingleton(sp =>
            new PluginPropertyHydrationService<ProcessInstanceRecord<TGlobalState>, TGlobalState>(
                sp.GetRequiredService<IPluginDocumentStore<ProcessInstanceRecord<TGlobalState>>>(),
                (workflow, _) => workflow.GlobalState));

        _hydrationStepDescriptors.Add(
            new GlobalStateHydrationDescriptor<TPlugin, ProcessInstanceRecord<TGlobalState>, TGlobalState>(
                setter,
                env => env.ProcessId.ToString(),
                env => env.ExecutableBlueprintId.ToString()));

        return this;
    }

    public Plugin<TPlugin> Build()
    {
        _hostAppBuilder.Services.AddSingleton(
            new PluginHydrationPlan<TPlugin>(_hydrationStepDescriptors.ToArray()));

        _hostAppBuilder.Services.AddSingleton(
            new PluginExecutionResultFactory<TPlugin>(
                _outputChannels.Values.ToList(),
                _globalStateSerializer));

        host = _hostAppBuilder.Build();

        return new Plugin<TPlugin>(this);
    }

    internal async Task RunAsync()
    {
        if (PluginConfigurationHelpWriter.ShouldShowHelp(Environment.GetCommandLineArgs()))
        {
            await PluginConfigurationHelpWriter.WriteAsync(Console.Out, _displayName, _capabilityTypes);
            return;
        }

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

    private void RegisterConfigurationType(Type optionsType)
    {
        if (!PluginConfigurationKey.IsDomainOptionsType(optionsType))
        {
            throw new InvalidOperationException(
                $"Plugin configuration type '{optionsType.FullName}' must live under '{PluginConfigurationKey.DomainNamespacePrefix}<Domain>'.");
        }

        if (!_configurationTypes.Add(optionsType))
        {
            return;
        }

        _hostAppBuilder.Services.AddSingleton(optionsType, sp =>
            PluginConfigurationBinder.Bind(
                optionsType,
                sp.GetRequiredService<IConfiguration>()));

        var optionsWrapperType = typeof(IOptions<>).MakeGenericType(optionsType);
        _hostAppBuilder.Services.AddSingleton(optionsWrapperType, sp =>
            PluginConfigurationBinder.CreateOptions(
                optionsType,
                sp.GetRequiredService(optionsType)));
    }

    private bool UseFileStateStore() =>
        string.Equals(
            _hostAppBuilder.Configuration["PluginStateStore:Mode"],
            "File",
            StringComparison.OrdinalIgnoreCase);

    private bool UseOtlpExporter() =>
        !string.Equals(
            _hostAppBuilder.Configuration["OTEL_SDK_DISABLED"],
            "true",
            StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(_hostAppBuilder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

    private string GetFileStateRootPath() =>
        _hostAppBuilder.Configuration["PluginStateStore:RootPath"]
        ?? throw new InvalidOperationException(
            "Plugin state store root path is not configured for file-based hydration.");

    private string GetCosmosDatabaseName() =>
        _hostAppBuilder.Configuration[WorkflowBuilderTopology.Cosmos.DatabaseNameKey]
        ?? WorkflowBuilderTopology.Cosmos.DefaultDatabaseName;

    private string GetCosmosConnectionString() =>
        _hostAppBuilder.Configuration[WorkflowBuilderTopology.Cosmos.ConnectionStringKey]
        ?? _hostAppBuilder.Configuration.GetConnectionString(
            _hostAppBuilder.Configuration[WorkflowBuilderTopology.Cosmos.ConnectionNameKey]
            ?? WorkflowBuilderTopology.Cosmos.DefaultDatabaseName)
        ?? _hostAppBuilder.Configuration.GetConnectionString(WorkflowBuilderTopology.Cosmos.DefaultDatabaseName)
        ?? throw new InvalidOperationException(
            "Cosmos DB connection string is not configured for the plugin harness.");

    private void EnsureCosmosHttpClientRegistered()
    {
        if (UseFileStateStore())
        {
            return;
        }

        _hostAppBuilder.Services.TryAddSingleton(_ =>
        {
            var connectionInfo = GetCosmosConnectionInfo();
            var handler = new HttpClientHandler();
            if (connectionInfo.DisableServerCertificateValidation ||
                IsLocalCosmosEndpoint(connectionInfo.Endpoint))
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            return new HttpClient(handler)
            {
                BaseAddress = connectionInfo.Endpoint,
                Timeout = TimeSpan.FromSeconds(10)
            };
        });
    }

    private CosmosConnectionInfo GetCosmosConnectionInfo()
    {
        var connectionString = GetCosmosConnectionString();
        var endpoint = GetConnectionStringSegment(connectionString, "AccountEndpoint");
        var accountKey = GetConnectionStringSegment(connectionString, "AccountKey");

        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException(
                "Cosmos DB connection string is missing a valid AccountEndpoint for the plugin harness.");
        }

        if (string.IsNullOrWhiteSpace(accountKey))
        {
            throw new InvalidOperationException(
                "Cosmos DB connection string is missing AccountKey for the plugin harness.");
        }

        var disableServerCertificateValidation = string.Equals(
            GetConnectionStringSegment(connectionString, "DisableServerCertificateValidation"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        return new CosmosConnectionInfo(
            endpointUri,
            accountKey,
            disableServerCertificateValidation);
    }

    private static bool IsLocalCosmosEndpoint(Uri uri)
    {
        return uri.IsLoopback ||
               uri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetConnectionStringSegment(string connectionString, string segmentName)
    {
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            if (segment[..separatorIndex].Equals(segmentName, StringComparison.OrdinalIgnoreCase))
            {
                return segment[(separatorIndex + 1)..];
            }
        }

        return null;
    }

    private void EnsureTaskDocumentStoreRegistered()
    {
        if (UseFileStateStore())
        {
            _hostAppBuilder.Services.TryAddSingleton<IPluginDocumentStore<ProcessStepRecord>>(
                _ => new FilePluginDocumentStore<ProcessStepRecord>(GetFileStateRootPath(), ProcessStepsContainerName));
            return;
        }

        EnsureCosmosHttpClientRegistered();
        var databaseName = GetCosmosDatabaseName();
        _hostAppBuilder.Services.TryAddSingleton<IPluginDocumentStore<ProcessStepRecord>>(sp =>
            new CosmosPluginDocumentStore<ProcessStepRecord>(
                sp.GetRequiredService<HttpClient>(),
                databaseName,
                ProcessStepsContainerName,
                GetCosmosConnectionInfo().AccountKey));
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

        EnsureCosmosHttpClientRegistered();
        var databaseName = GetCosmosDatabaseName();
        _hostAppBuilder.Services.TryAddSingleton<IPluginDocumentStore<ProcessInstanceRecord<TGlobalState>>>(sp =>
            new CosmosPluginDocumentStore<ProcessInstanceRecord<TGlobalState>>(
                sp.GetRequiredService<HttpClient>(),
                databaseName,
                ProcessesContainerName,
                GetCosmosConnectionInfo().AccountKey));
    }

    private sealed record CosmosConnectionInfo(
        Uri Endpoint,
        string AccountKey,
        bool DisableServerCertificateValidation);

    private static void AddConfigFiles(IConfigurationBuilder configuration, IReadOnlyList<string> args)
    {
        foreach (var configPath in PluginConfigurationArgumentParser.GetConfigPaths(args))
        {
            configuration.AddJsonFile(configPath, optional: false, reloadOnChange: false);
        }
    }

}

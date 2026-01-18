using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Plugins.Harness;

public class PluginBuilder<TPlugin>
    where TPlugin : class, IPlugin
{
    private readonly string _displayName;
    private readonly Dictionary<string, PluginChannel> _inputChannels = new();
    private readonly Dictionary<string, PluginChannel> _outputChannels = new();
    private readonly HostApplicationBuilder _hostAppBuilder;
    private readonly PluginStateConfiguration<TPlugin> _stateConfiguration = new();
    private IHost? host = null;

    private Type? _globalStateType;

    public IServiceCollection Services => _hostAppBuilder.Services;

    public PluginBuilder(string displayName)
    {
        _hostAppBuilder = Host.CreateApplicationBuilder(Environment.GetCommandLineArgs());
        _hostAppBuilder.Services.AddHostedService<EventHandler<TPlugin>>();
        _hostAppBuilder.Services.AddSingleton(_stateConfiguration);
        _hostAppBuilder.Logging.ClearProviders().AddSimpleConsole(options =>
        {
        });
        _displayName = displayName;
    }

    public PluginBuilder<TPlugin> ConfigureInputChannel<TChannelType>(
    Expression<Func<TPlugin, TChannelType>> propertySelector)
    {
        var property = GetPropertyInfo(propertySelector);
        var name = property.Name;
        var getter = propertySelector.Compile();
        var setter = CreateSetter<TChannelType>(property);
        if (_inputChannels.ContainsKey(name))
            throw new InvalidOperationException($"Input channel '{name}' is already configured.");

        _inputChannels.Add(name, new PluginChannel<TPlugin, TChannelType>(name, getter));
        _stateConfiguration.AddInputChannel(name, setter);
        return this;
    }

    public PluginBuilder<TPlugin> ConfigureOutputChannel<TChannelType>(
        Expression<Func<TPlugin, TChannelType>> propertySelector)
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
        var property = GetPropertyInfo(propertySelector);
        var setter = CreateSetter<TGlobalState>(property);
        _globalStateType = typeof(TGlobalState);
        _stateConfiguration.SetGlobalState(setter);
        return this;
    }

    public Plugin<TPlugin> Build()
    {
        // Generate files first (Debug-only), then build the host and log
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
                inputChannels: _inputChannels.Values.Select(c => new KeyValuePair<string, Type>(c.Name, c.MessageType)),
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
        //var plugin = ActivatorUtilities.CreateInstance<TPlugin>(host!.Services);
        //await plugin.Execute();
        await host!.RunAsync();
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

    private static Action<TPlugin, TChannelType> CreateSetter<TChannelType>(PropertyInfo property)
    {
        if (property.SetMethod is null)
            throw new InvalidOperationException($"Property '{property.Name}' must be settable.");

        var pluginParam = Expression.Parameter(typeof(TPlugin), "plugin");
        var valueParam = Expression.Parameter(typeof(TChannelType), "value");

        try
        {
            var assign = Expression.Assign(Expression.Property(pluginParam, property), valueParam);
            return Expression.Lambda<Action<TPlugin, TChannelType>>(assign, pluginParam, valueParam).Compile();
        }
        catch (ArgumentException)
        {
            return (plugin, value) => property.SetValue(plugin, value);
        }
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
}

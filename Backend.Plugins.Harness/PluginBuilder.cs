using System.Linq.Expressions;
using System.Reflection;
using Backend.Plugins.Communication;
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
    private readonly HostApplicationBuilder _hostAppBuilder = Host.CreateApplicationBuilder();
    private IHost? host = null;

    private Type? _globalStateType;

    public IServiceCollection Services => _hostAppBuilder.Services;

    public PluginBuilder(string displayName)
    {
        _hostAppBuilder.Logging.ClearProviders().AddSimpleConsole();
        _hostAppBuilder.Services.AddSingleton<SignalInbox>();
        _hostAppBuilder.Services.AddHostedService<SignalMonitoringService>();
        _displayName = displayName;
    }

    public PluginBuilder<TPlugin> ConfigureInputChannel<TChannelType>(
    Expression<Func<TPlugin, TChannelType>> propertySelector)
    {
        var (name, getter) = GetNameAndGetter(propertySelector);
        if (_inputChannels.ContainsKey(name))
            throw new InvalidOperationException($"Input channel '{name}' is already configured.");

        _inputChannels.Add(name, new PluginChannel<TPlugin, TChannelType>(name, getter));
        return this;
    }

    public PluginBuilder<TPlugin> ConfigureOutputChannel<TChannelType>(
        Expression<Func<TPlugin, TChannelType>> propertySelector)
    {
        var (name, getter) = GetNameAndGetter(propertySelector);
        if (_outputChannels.ContainsKey(name))
            throw new InvalidOperationException($"Output channel '{name}' is already configured.");

        _outputChannels.Add(name, new PluginChannel<TPlugin, TChannelType>(name, getter));
        return this;
    }

    public PluginBuilder<TPlugin> WithGlobalState<TGlobalState>(
        Expression<Func<TPlugin, TGlobalState>> propertySelector)
        where TGlobalState : class, IGlobalState
    {
        _globalStateType = typeof(TGlobalState);
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
        var plugin = ActivatorUtilities.CreateInstance<TPlugin>(host!.Services);
        await plugin.Execute();
        await host!.RunAsync();
    }

    private IEnumerable<Type> EnumerateRootTypes()
    {
        foreach (var c in _inputChannels.Values) yield return c.MessageType;
        foreach (var c in _outputChannels.Values) yield return c.MessageType;
        if (_globalStateType is not null) yield return _globalStateType;
    }

    private static (string name, Func<TPlugin, TChannelType> getter) GetNameAndGetter<TChannelType>(
        Expression<Func<TPlugin, TChannelType>> expr)
    {
        if (expr.Body is not MemberExpression m || m.Member is not PropertyInfo pi)
            throw new ArgumentException("Selector must be p => p.Property");
        return (pi.Name, expr.Compile());
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

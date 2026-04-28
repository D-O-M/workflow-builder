using Backend.Plugins.Communication;
using System.Text.Json;

namespace Backend.Plugins.Harness;

internal sealed class PluginHydrationContext
{
    public Dictionary<string, JsonElement> EffectiveInputData { get; } = new(StringComparer.Ordinal);
}

internal interface IPluginHydrationStep<in TPlugin>
    where TPlugin : class, IPlugin
{
    /// <summary>
    /// Executes hydration against the provided plugin instance.
    /// </summary>
    Task HydrateAsync(
        TPlugin plugin,
        InvocationEnvelope envelope,
        PluginHydrationContext context,
        CancellationToken cancellationToken);
}

internal sealed class InputChannelHydrationStep<TPlugin, TProperty> : IPluginHydrationStep<TPlugin>
    where TPlugin : class, IPlugin
    where TProperty : class
{
    private readonly string _channelName;
    private readonly InputChannelHydrationService<TProperty> _hydrator;
    private readonly PropertySetterUtility<TPlugin, TProperty> _setter;

    public InputChannelHydrationStep(
        string channelName,
        InputChannelHydrationService<TProperty> hydrator,
        PropertySetterUtility<TPlugin, TProperty> setter)
    {
        _channelName = channelName;
        _hydrator = hydrator;
        _setter = setter;
    }

    public async Task HydrateAsync(
        TPlugin plugin,
        InvocationEnvelope envelope,
        PluginHydrationContext context,
        CancellationToken cancellationToken)
    {
        var hydrated = await _hydrator.HydrateAsync(
            envelope,
            _channelName,
            cancellationToken);

        _setter.Set(plugin, hydrated.Value);
        context.EffectiveInputData[_channelName] = hydrated.EffectiveInput.Clone();
    }
}

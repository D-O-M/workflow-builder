using Backend.Plugins.Communication;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Backend.Plugins.Harness;

internal sealed class PluginHydrationContext
{
    public Dictionary<string, JsonElement> EffectiveInputData { get; } = new(StringComparer.Ordinal);
}

internal sealed class PluginHydrationPlan<TPlugin>
    where TPlugin : class, IPlugin
{
    private readonly IReadOnlyList<IPluginHydrationStepDescriptor<TPlugin>> _steps;

    public PluginHydrationPlan(IReadOnlyList<IPluginHydrationStepDescriptor<TPlugin>> steps)
    {
        _steps = steps;
    }

    public static PluginHydrationPlan<TPlugin> Empty { get; } = new([]);

    public async Task HydrateAsync(
        IServiceProvider services,
        TPlugin plugin,
        InvocationEnvelope envelope,
        PluginHydrationContext context,
        CancellationToken cancellationToken)
    {
        foreach (var step in _steps)
        {
            await step.HydrateAsync(
                services,
                plugin,
                envelope,
                context,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

internal interface IPluginHydrationStepDescriptor<in TPlugin>
    where TPlugin : class, IPlugin
{
    Task HydrateAsync(
        IServiceProvider services,
        TPlugin plugin,
        InvocationEnvelope envelope,
        PluginHydrationContext context,
        CancellationToken cancellationToken);
}

internal sealed class InputChannelHydrationDescriptor<TPlugin, TProperty> : IPluginHydrationStepDescriptor<TPlugin>
    where TPlugin : class, IPlugin
    where TProperty : class
{
    private readonly string _channelName;
    private readonly PropertySetterUtility<TPlugin, TProperty> _setter;

    public InputChannelHydrationDescriptor(
        string channelName,
        PropertySetterUtility<TPlugin, TProperty> setter)
    {
        _channelName = channelName;
        _setter = setter;
    }

    public async Task HydrateAsync(
        IServiceProvider services,
        TPlugin plugin,
        InvocationEnvelope envelope,
        PluginHydrationContext context,
        CancellationToken cancellationToken)
    {
        var hydrator = services.GetRequiredService<InputChannelHydrationService<TProperty>>();
        var hydrated = await hydrator.HydrateAsync(
            envelope,
            _channelName,
            cancellationToken).ConfigureAwait(false);

        _setter.Set(plugin, hydrated.Value);
        context.EffectiveInputData[_channelName] = hydrated.EffectiveInput.Clone();
    }
}

internal sealed class GlobalStateHydrationDescriptor<TPlugin, TDocument, TProperty> : IPluginHydrationStepDescriptor<TPlugin>
    where TPlugin : class, IPlugin
    where TDocument : class
    where TProperty : class
{
    private readonly PropertySetterUtility<TPlugin, TProperty> _setter;
    private readonly Func<InvocationEnvelope, string> _documentIdSelector;
    private readonly Func<InvocationEnvelope, string> _partitionKeySelector;

    public GlobalStateHydrationDescriptor(
        PropertySetterUtility<TPlugin, TProperty> setter,
        Func<InvocationEnvelope, string> documentIdSelector,
        Func<InvocationEnvelope, string> partitionKeySelector)
    {
        _setter = setter;
        _documentIdSelector = documentIdSelector;
        _partitionKeySelector = partitionKeySelector;
    }

    public Task HydrateAsync(
        IServiceProvider services,
        TPlugin plugin,
        InvocationEnvelope envelope,
        PluginHydrationContext context,
        CancellationToken cancellationToken)
    {
        var hydrator = services.GetRequiredService<PluginPropertyHydrationService<TDocument, TProperty>>();
        return hydrator.HydratePluginProperty(
            value => _setter.Set(plugin, value),
            _documentIdSelector(envelope),
            _partitionKeySelector(envelope),
            outputChannelName: string.Empty,
            cancellationToken);
    }
}

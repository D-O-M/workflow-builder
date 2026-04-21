using Backend.Plugins.Communication;

namespace Backend.Plugins.Harness;

internal interface IPluginHydrationStep<in TPlugin>
    where TPlugin : class, IPlugin
{
    /// <summary>
    /// Executes hydration against the provided plugin instance.
    /// </summary>
    Task HydrateAsync(
        TPlugin plugin,
        InvocationEnvelope envelope,
        CancellationToken cancellationToken);
}

internal sealed class PluginHydrationStep<TPlugin, TDocument, TProperty> : IPluginHydrationStep<TPlugin>
    where TPlugin : class, IPlugin
    where TDocument : class
{
    private readonly string _channelName;
    private readonly PluginPropertyHydrationService<TDocument, TProperty> _hydrator;
    private readonly PropertySetterUtility<TPlugin, TProperty> _setter;
    private readonly Func<InvocationEnvelope, string> _documentIdSelector;
    private readonly Func<InvocationEnvelope, string> _partitionKeySelector;

    public PluginHydrationStep(
        string channelName,
        PluginPropertyHydrationService<TDocument, TProperty> hydrator,
        PropertySetterUtility<TPlugin, TProperty> setter,
        Func<InvocationEnvelope, string> documentIdSelector,
        Func<InvocationEnvelope, string> partitionKeySelector)
    {
        _channelName = channelName;
        _hydrator = hydrator;
        _setter = setter;
        _documentIdSelector = documentIdSelector;
        _partitionKeySelector = partitionKeySelector;
    }

    public Task HydrateAsync(
        TPlugin plugin,
        InvocationEnvelope envelope,
        CancellationToken cancellationToken)
        => _hydrator.HydratePluginProperty(
            propertySetter: value => _setter.Set(plugin, value),
            documentId: _documentIdSelector(envelope),
            partitionKey: _partitionKeySelector(envelope),
            outputChannelName: _channelName,
            cancellationToken: cancellationToken);
}

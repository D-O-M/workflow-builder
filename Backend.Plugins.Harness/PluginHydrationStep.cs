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

    // Extracts IDs from the envelope
    private readonly Func<InvocationEnvelope, Guid> _documentIdSelector;
    private readonly Func<InvocationEnvelope, Guid> _partitionKeySelector;

    public PluginHydrationStep(
        string channelName,
        PluginPropertyHydrationService<TDocument, TProperty> hydrator,
        PropertySetterUtility<TPlugin, TProperty> setter,
        Func<InvocationEnvelope, Guid> documentIdSelector,
        Func<InvocationEnvelope, Guid> partitionKeySelector)
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
    {
        var documentId = _documentIdSelector(envelope);
        var partitionKey = _partitionKeySelector(envelope);

        return _hydrator.HydratePluginProperty(
            propertySetter: value => _setter.Set(plugin, value),
            documentId: documentId,
            partitionKey: partitionKey,
            outputChannelName: envelope.PreviousTaskDetails.LinkedOutputChannelName,
            cancellationToken: cancellationToken);
    }
}

namespace Backend.Plugins.Harness;

internal sealed class PluginPropertyHydrationService<TDocument, TProperty>
    where TDocument : class
{
    private readonly IPluginDocumentStore<TDocument> _documentStore;
    private readonly Func<TDocument, string, TProperty> _documentValueExtractionFunction;
    private readonly TimeSpan _hydrationTimeout;
    private readonly bool _diagnosticsEnabled;

    public PluginPropertyHydrationService(
        IPluginDocumentStore<TDocument> documentStore,
        Func<TDocument, string, TProperty> documentValueExtractionFunction)
    {
        _documentStore = documentStore;
        _documentValueExtractionFunction = documentValueExtractionFunction;
        _hydrationTimeout = GetHydrationTimeout();
        _diagnosticsEnabled = string.Equals(
            Environment.GetEnvironmentVariable("PluginRuntime__Diagnostics"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task HydratePluginProperty(
        Action<TProperty> propertySetter,
        string documentId,
        string partitionKey,
        string outputChannelName,
        CancellationToken cancellationToken)
    {
        WriteDiagnostic(
            $"Hydrating {typeof(TProperty).Name} from {typeof(TDocument).Name} document '{documentId}' partition '{partitionKey}' channel '{outputChannelName}'.");

        var document = await _documentStore
            .GetAsync(documentId, partitionKey, cancellationToken)
            .WaitAsync(_hydrationTimeout, cancellationToken);

        if (document is null)
        {
            throw new PluginDocumentNotFoundException(typeof(TDocument), documentId);
        }

        var value = _documentValueExtractionFunction(document, outputChannelName);
        propertySetter(value);

        WriteDiagnostic(
            $"Hydrated {typeof(TProperty).Name} from {typeof(TDocument).Name} document '{documentId}'.");
    }

    private static TimeSpan GetHydrationTimeout()
    {
        var configuredValue = Environment.GetEnvironmentVariable("PluginRuntime__HydrationTimeoutSeconds");
        return int.TryParse(configuredValue, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(30);
    }

    private void WriteDiagnostic(string message)
    {
        if (!_diagnosticsEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[plugin-harness] {DateTimeOffset.UtcNow:O} {message}");
    }
}

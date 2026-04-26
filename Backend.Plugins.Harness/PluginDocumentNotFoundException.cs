namespace Backend.Plugins.Harness;

public sealed class PluginDocumentNotFoundException : Exception
{
    public PluginDocumentNotFoundException(Type documentType, string documentId)
        : base($"Plugin hydration document of type '{documentType.Name}' with ID '{documentId}' could not be found.")
    {
    }
}

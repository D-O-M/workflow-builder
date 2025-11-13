namespace Backend.Interfaces.Utilities;

public interface IPluginWorkspaceFactoryUtility
{
    Task<string> CreateWorkspaceAsync(
        string pluginName,
        Version pluginVersion,
        CancellationToken cancellationToken);
}

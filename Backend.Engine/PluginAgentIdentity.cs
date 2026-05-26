using Backend.Models;

namespace Backend.Engine;

public sealed record PluginAgentIdentity(
    Guid AgentInstanceId,
    int TaskVersionId,
    int TaskDefinitionId,
    string PluginName,
    Version PluginVersion)
{
    public string DisplayName => $"{PluginName} (Version: {PluginVersion})";

    public static PluginAgentIdentity FromTaskVersion(TaskDefinitionInfo taskInfo) =>
        new(
            Guid.NewGuid(),
            taskInfo.Id,
            taskInfo.TaskDefinitionId,
            taskInfo.Name,
            taskInfo.Version);

    public static PluginAgentIdentity Unknown(string pluginName) =>
        new(
            Guid.Empty,
            TaskVersionId: 0,
            TaskDefinitionId: 0,
            string.IsNullOrWhiteSpace(pluginName) ? "UnknownPlugin" : pluginName,
            new Version(0, 0, 0));
}

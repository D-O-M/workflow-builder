using System.Text.Json;
using Backend.Plugins.Communication;

namespace Backend.Plugins.Harness;

internal sealed class PluginExecutionResultFactory<TPlugin>
    where TPlugin : class, IPlugin
{
    private readonly IReadOnlyCollection<IPluginOutputChannel<TPlugin>> _outputChannels;

    public PluginExecutionResultFactory(IReadOnlyCollection<IPluginOutputChannel<TPlugin>> outputChannels)
    {
        _outputChannels = outputChannels;
    }

    public PluginExecutionResult CreateSuccess(
        TPlugin plugin,
        InvocationEnvelope envelope,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        Dictionary<string, double>? timingsMs,
        IReadOnlyDictionary<string, JsonElement>? effectiveInputData)
    {
        return new PluginExecutionResult(
            envelope.ProcessStepId,
            true,
            CloneEffectiveInputData(effectiveInputData),
            BuildOutputData(plugin),
            startedAt,
            completedAt,
            timingsMs,
            ErrorMessage: null);
    }

    public PluginExecutionResult CreateFailure(
        TPlugin plugin,
        InvocationEnvelope envelope,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        Exception exception,
        IReadOnlyDictionary<string, JsonElement>? effectiveInputData,
        Dictionary<string, double>? timingsMs = null)
    {
        return new PluginExecutionResult(
            envelope.ProcessStepId,
            false,
            CloneEffectiveInputData(effectiveInputData),
            BuildOutputData(plugin),
            startedAt,
            completedAt,
            timingsMs,
            exception.Message);
    }

    private Dictionary<string, JsonElement> BuildOutputData(TPlugin plugin)
    {
        var outputData = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var outputChannel in _outputChannels)
        {
            var serialized = outputChannel.Serialize(plugin);
            if (serialized is null)
            {
                continue;
            }

            outputData[outputChannel.Name] = serialized.Value;
        }

        return outputData;
    }

    private static Dictionary<string, JsonElement>? CloneEffectiveInputData(
        IReadOnlyDictionary<string, JsonElement>? effectiveInputData)
    {
        if (effectiveInputData is null || effectiveInputData.Count == 0)
        {
            return null;
        }

        return effectiveInputData.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Clone(),
            StringComparer.Ordinal);
    }
}

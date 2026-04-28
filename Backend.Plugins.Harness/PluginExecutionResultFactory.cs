using System.Text.Json;
using Backend.Plugins.Communication;

namespace Backend.Plugins.Harness;

internal sealed class PluginExecutionResultFactory<TPlugin>
    where TPlugin : class, IPlugin
{
    private static readonly JsonSerializerOptions ContractJsonOptions = new(JsonSerializerDefaults.General);

    private readonly Func<TPlugin, object?>? _globalStateGetter;
    private readonly IReadOnlyCollection<PluginChannel> _outputChannels;

    public PluginExecutionResultFactory(
        IReadOnlyCollection<PluginChannel> outputChannels,
        Func<TPlugin, object?>? globalStateGetter)
    {
        _outputChannels = outputChannels;
        _globalStateGetter = globalStateGetter;
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
            BuildGlobalState(plugin),
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
            BuildGlobalState(plugin),
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
            var value = outputChannel.GetValue(plugin);
            if (value is null)
            {
                continue;
            }

            outputData[outputChannel.Name] = JsonSerializer.SerializeToElement(
                value,
                value.GetType(),
                ContractJsonOptions);
        }

        return outputData;
    }

    private Dictionary<string, JsonElement>? BuildGlobalState(TPlugin plugin)
    {
        if (_globalStateGetter is null)
        {
            return null;
        }

        var value = _globalStateGetter(plugin);
        if (value is null)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                   JsonSerializer.Serialize(value, value.GetType(), ContractJsonOptions),
                   ContractJsonOptions)
               ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
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

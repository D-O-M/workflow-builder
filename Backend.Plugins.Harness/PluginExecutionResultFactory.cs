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
        DateTimeOffset completedAt)
    {
        return new PluginExecutionResult(
            envelope.ProcessStepId,
            true,
            BuildOutputData(plugin),
            BuildGlobalState(plugin),
            startedAt,
            completedAt,
            ErrorMessage: null);
    }

    public PluginExecutionResult CreateFailure(
        TPlugin plugin,
        InvocationEnvelope envelope,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        Exception exception)
    {
        return new PluginExecutionResult(
            envelope.ProcessStepId,
            false,
            BuildOutputData(plugin),
            BuildGlobalState(plugin),
            startedAt,
            completedAt,
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
}

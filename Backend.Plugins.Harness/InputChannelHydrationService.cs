using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Backend.Database.State.Objects;
using Backend.Plugins.Communication;

namespace Backend.Plugins.Harness;

internal sealed class InputChannelHydrationService<TChannelType>
    where TChannelType : class
{
    private readonly IPluginDocumentStore<ProcessStepRecord> _stepStore;
    private readonly JsonTypeInfo<TChannelType> _jsonTypeInfo;
    private readonly IPluginInputMaterializer<TChannelType>? _materializer;
    private readonly TimeSpan _hydrationTimeout;
    private readonly bool _diagnosticsEnabled;

    public InputChannelHydrationService(
        IPluginDocumentStore<ProcessStepRecord> stepStore,
        JsonTypeInfo<TChannelType> jsonTypeInfo,
        IPluginInputMaterializer<TChannelType>? materializer = null)
    {
        _stepStore = stepStore;
        _jsonTypeInfo = jsonTypeInfo;
        _materializer = materializer;
        _hydrationTimeout = GetHydrationTimeout();
        _diagnosticsEnabled = string.Equals(
            Environment.GetEnvironmentVariable("PluginRuntime__Diagnostics"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<InputChannelHydrationResult<TChannelType>> HydrateAsync(
        InvocationEnvelope envelope,
        string channelName,
        CancellationToken cancellationToken)
    {
        var plan = envelope.InputHydrationPlan;
        if (!string.Equals(plan.InputChannelName, channelName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invocation hydration plan targets input channel '{plan.InputChannelName}', but plugin requested '{channelName}'.");
        }

        WriteDiagnostic(
            $"Hydrating {typeof(TChannelType).Name} for step {envelope.ProcessStepId} from plan with {plan.Sources.Count} source(s).");

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in plan.InitialInput)
        {
            merged[key] = value;
        }

        foreach (var source in plan.Sources)
        {
            if (source.InlineInput is not null)
            {
                foreach (var (key, value) in source.InlineInput)
                {
                    merged[key] = value;
                }

                continue;
            }

            var sourceStep = await _stepStore
                .GetAsync(source.SourceProcessStepId, envelope.ProcessId.ToString(), cancellationToken)
                .WaitAsync(_hydrationTimeout, cancellationToken);

            if (sourceStep is null)
            {
                throw new InvalidOperationException(
                    $"Source process step '{source.SourceProcessStepId}' for binding '{source.BindingId}' was not found.");
            }

            if (sourceStep.OutputData is null ||
                !sourceStep.OutputData.TryGetValue(source.SourceOutputChannelName, out var sourcePayload))
            {
                throw new InvalidOperationException(
                    $"Source process step '{source.SourceProcessStepId}' does not contain output channel '{source.SourceOutputChannelName}' for binding '{source.BindingId}'.");
            }

            ApplySourceMappings(sourcePayload, source.PropertyMappings, merged);
        }

        ApplyOverrides(merged, plan.DesignTimeInputOverrides);
        ApplyOverrides(merged, plan.RuntimeInputOverrides);

        var effectiveInput = JsonSerializer.SerializeToElement(
            merged,
            PluginJsonSerialization.InputJsonOptions);
        var typedValue = _materializer is not null
            ? _materializer.Materialize(merged)
            : effectiveInput.Deserialize(_jsonTypeInfo)
              ?? throw new InvalidOperationException(
                  $"Unable to hydrate input channel '{channelName}' as {typeof(TChannelType).FullName}.");

        WriteDiagnostic(
            $"Hydrated {typeof(TChannelType).Name} for step {envelope.ProcessStepId}.");

        return new InputChannelHydrationResult<TChannelType>(
            typedValue,
            effectiveInput);
    }

    private static void ApplySourceMappings(
        JsonElement sourcePayload,
        IReadOnlyCollection<InputHydrationPropertyMapping> mappings,
        IDictionary<string, JsonElement> target)
    {
        if (sourcePayload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        List<JsonProperty>? sourceProperties = null;

        foreach (var mapping in mappings)
        {
            if (sourcePayload.TryGetProperty(mapping.SourceProperty, out var value))
            {
                target[mapping.TargetProperty] = value;
                continue;
            }

            sourceProperties ??= sourcePayload.EnumerateObject().ToList();
            if (TryGetCompatibleSourceProperty(sourceProperties, mapping.SourceProperty, out value))
            {
                target[mapping.TargetProperty] = value;
                continue;
            }

            throw new InvalidOperationException(
                $"Source property '{mapping.SourceProperty}' required for target property '{mapping.TargetProperty}' was not found.");
        }
    }

    private static void ApplyOverrides(
        IDictionary<string, JsonElement> target,
        IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        if (overrides is null)
        {
            return;
        }

        foreach (var (key, value) in overrides)
        {
            target[key] = value;
        }
    }

    private static bool TryGetCompatibleSourceProperty(
        IReadOnlyCollection<JsonProperty> sourceProperties,
        string sourceProperty,
        out JsonElement value)
    {
        foreach (var property in sourceProperties)
        {
            if (string.Equals(property.Name, sourceProperty, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
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

internal sealed record InputChannelHydrationResult<TChannelType>(
    TChannelType Value,
    JsonElement EffectiveInput)
    where TChannelType : class;

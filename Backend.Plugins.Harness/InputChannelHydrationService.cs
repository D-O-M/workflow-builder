using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Plugins.Communication;

namespace Backend.Plugins.Harness;

internal sealed class InputChannelHydrationService<TChannelType>
    where TChannelType : class
{
    private readonly IPluginDocumentStore<ProcessStepRecord> _stepStore;
    private readonly TimeSpan _hydrationTimeout;
    private readonly bool _diagnosticsEnabled;

    public InputChannelHydrationService(IPluginDocumentStore<ProcessStepRecord> stepStore)
    {
        _stepStore = stepStore;
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
            merged[key] = value.Clone();
        }

        foreach (var source in plan.Sources)
        {
            if (source.InlineInput is not null)
            {
                foreach (var (key, value) in source.InlineInput)
                {
                    merged[key] = value.Clone();
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

            foreach (var (key, value) in MaterializePartialInput(sourcePayload, source.PropertyMappings))
            {
                merged[key] = value.Clone();
            }
        }

        ApplyOverrides(merged, plan.DesignTimeInputOverrides);
        ApplyOverrides(merged, plan.RuntimeInputOverrides);

        var effectiveInput = JsonSerializer.SerializeToElement(merged, JsonSerializerOptions.Web);
        var typedValue = effectiveInput.Deserialize<TChannelType>(JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException(
                $"Unable to hydrate input channel '{channelName}' as {typeof(TChannelType).FullName}.");

        WriteDiagnostic(
            $"Hydrated {typeof(TChannelType).Name} for step {envelope.ProcessStepId}.");

        return new InputChannelHydrationResult<TChannelType>(
            typedValue,
            effectiveInput);
    }

    private static Dictionary<string, JsonElement> MaterializePartialInput(
        JsonElement sourcePayload,
        IReadOnlyCollection<InputHydrationPropertyMapping> mappings)
    {
        var materialized = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (sourcePayload.ValueKind != JsonValueKind.Object)
        {
            return materialized;
        }

        var sourceProperties = sourcePayload.EnumerateObject().ToList();
        var exactSourceProperties = sourceProperties
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);

        foreach (var mapping in mappings)
        {
            if (exactSourceProperties.TryGetValue(mapping.SourceProperty, out var value) ||
                TryGetCompatibleSourceProperty(sourceProperties, mapping.SourceProperty, out value))
            {
                materialized[mapping.TargetProperty] = value.Clone();
                continue;
            }

            throw new InvalidOperationException(
                $"Source property '{mapping.SourceProperty}' required for target property '{mapping.TargetProperty}' was not found.");
        }

        return materialized;
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
            target[key] = value.Clone();
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

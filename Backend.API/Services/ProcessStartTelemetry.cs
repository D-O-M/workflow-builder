using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Backend.API.Services;

public sealed class ProcessStartTelemetry
{
    private static readonly Meter Meter = new("WorkflowBuilder.API.ProcessStart", "1.0.0");
    private static readonly Histogram<double> ResolveDefinitionMs = Meter.CreateHistogram<double>("process_start_resolve_definition_ms");
    private static readonly Histogram<double> LoadGraphMs = Meter.CreateHistogram<double>("process_start_load_graph_ms");
    private static readonly Histogram<double> ValidateOverridesMs = Meter.CreateHistogram<double>("process_start_validate_overrides_ms");
    private static readonly Histogram<double> MaterializeInitialInputMs = Meter.CreateHistogram<double>("process_start_materialize_initial_input_ms");
    private static readonly Histogram<double> CreateProcessStateMs = Meter.CreateHistogram<double>("process_start_create_process_state_ms");
    private static readonly Histogram<double> CreateInitialStepMs = Meter.CreateHistogram<double>("process_start_create_initial_step_ms");
    private static readonly Histogram<double> EnqueueInitialStepMs = Meter.CreateHistogram<double>("process_start_enqueue_initial_step_ms");
    private static readonly Histogram<double> TotalMs = Meter.CreateHistogram<double>("process_start_total_ms");

    public TimedOperation StartTotal(int blueprintDefinitionId) =>
        new(TotalMs, BuildTags(blueprintDefinitionId));

    public TimedOperation StartResolveDefinition(int blueprintDefinitionId) =>
        new(ResolveDefinitionMs, BuildTags(blueprintDefinitionId));

    public TimedOperation StartLoadGraph(int blueprintDefinitionId) =>
        new(LoadGraphMs, BuildTags(blueprintDefinitionId));

    public TimedOperation StartValidateOverrides(int blueprintDefinitionId) =>
        new(ValidateOverridesMs, BuildTags(blueprintDefinitionId));

    public TimedOperation StartMaterializeInitialInput(int blueprintDefinitionId) =>
        new(MaterializeInitialInputMs, BuildTags(blueprintDefinitionId));

    public TimedOperation StartCreateProcessState(int blueprintDefinitionId) =>
        new(CreateProcessStateMs, BuildTags(blueprintDefinitionId));

    public TimedOperation StartCreateInitialStep(int blueprintDefinitionId) =>
        new(CreateInitialStepMs, BuildTags(blueprintDefinitionId));

    public TimedOperation StartEnqueueInitialStep(int blueprintDefinitionId) =>
        new(EnqueueInitialStepMs, BuildTags(blueprintDefinitionId));

    private static TagList BuildTags(int blueprintDefinitionId) =>
        new()
        {
            { "blueprint.definition_id", blueprintDefinitionId.ToString() }
        };

    public readonly struct TimedOperation : IDisposable
    {
        private readonly Histogram<double> _histogram;
        private readonly TagList _tags;
        private readonly long _startedAt;

        internal TimedOperation(Histogram<double> histogram, TagList tags)
        {
            _histogram = histogram;
            _tags = tags;
            _startedAt = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            _histogram.Record(
                Math.Max(0, Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds),
                _tags);
        }
    }
}

using System.Diagnostics.Metrics;
using System.Threading;

namespace Backend.Engine;

public sealed class EngineProjectionTelemetry
{
    public const string MeterName = "WorkflowBuilder.Engine.Projections";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static long _synchronousProjectionUpdatesEnabled;

    static EngineProjectionTelemetry()
    {
        Meter.CreateObservableGauge(
            "engine_synchronous_projection_updates_enabled",
            () => Volatile.Read(ref _synchronousProjectionUpdatesEnabled));
    }

    public EngineProjectionTelemetry(EngineProjectionOptions options)
    {
        Volatile.Write(
            ref _synchronousProjectionUpdatesEnabled,
            options.SynchronousEngineUpdatesEnabled ? 1 : 0);
    }
}

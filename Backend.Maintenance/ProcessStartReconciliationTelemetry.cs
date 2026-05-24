using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Backend.Maintenance;

public sealed class ProcessStartReconciliationTelemetry
{
    public const string MeterName = "WorkflowBuilder.ProcessStartReconciliation";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> MissingInitialStepsFound =
        Meter.CreateCounter<long>("process_start_reconciliation_missing_initial_steps_found_total");
    private static readonly Counter<long> InitialStepsRepaired =
        Meter.CreateCounter<long>("process_start_reconciliation_initial_steps_repaired_total");
    private static readonly Counter<long> DispatchIntentsRepaired =
        Meter.CreateCounter<long>("process_start_reconciliation_dispatch_intents_repaired_total");
    private static readonly Counter<long> Failures =
        Meter.CreateCounter<long>("process_start_reconciliation_failures_total");

    public void RecordMissingInitialStep() =>
        MissingInitialStepsFound.Add(1);

    public void RecordInitialStepRepaired() =>
        InitialStepsRepaired.Add(1);

    public void RecordDispatchIntentRepaired() =>
        DispatchIntentsRepaired.Add(1);

    public void RecordFailure(string reason)
    {
        var tags = new TagList
        {
            { "failure.reason", reason }
        };
        Failures.Add(1, tags);
    }

    public void RecordFailure(Exception exception)
    {
        var tags = new TagList
        {
            { "exception.type", exception.GetType().FullName ?? exception.GetType().Name }
        };
        Failures.Add(1, tags);
    }
}

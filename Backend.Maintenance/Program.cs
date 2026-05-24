using Backend.Maintenance;
using Backend.Repositories.Processes;
using Backend.Services.ProcessStepQueue;
using Backend.Topology;
using OpenTelemetry.Metrics;

AspireEndpointEnvironment.PreferAspireHttpPortsOverInheritedUrls();

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
WorkflowBuilderTopologyValidator.ValidateMaintenance(builder.Configuration);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(Backend.Engine.ProcessLifecycleTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics
        .AddMeter("WorkflowBuilder.Cosmos")
        .AddMeter(ProcessStepDispatchTelemetry.MeterName)
        .AddMeter(ProcessSummaryProjectionTelemetry.MeterName)
        .AddMeter(ProcessRealtimeProjectionTelemetry.MeterName)
        .AddMeter(ProcessStartReconciliationTelemetry.MeterName)
        .AddMeter(StepFinalizationRecoveryTelemetry.MeterName)
        .AddMeter(Backend.Engine.ProcessLifecycleTelemetry.ActivitySourceName));

builder.Services.AddMaintenanceInfrastructure(
    builder.Configuration,
    builder.Environment);
builder.Services.AddMaintenanceWorkers(
    builder.Configuration);

await builder.Build().RunAsync();

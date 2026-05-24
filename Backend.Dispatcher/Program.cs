using Backend.Dispatcher;
using Backend.Repositories.Processes;
using Backend.Services.ProcessStepQueue;
using Backend.Topology;
using OpenTelemetry.Metrics;

AspireEndpointEnvironment.PreferAspireHttpPortsOverInheritedUrls();

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
WorkflowBuilderTopologyValidator.ValidateDispatcher(builder.Configuration);

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("WorkflowBuilder.Cosmos")
        .AddMeter(ProcessStepDispatchTelemetry.MeterName)
        .AddMeter(ProcessSummaryProjectionTelemetry.MeterName)
        .AddMeter(ProcessRealtimeProjectionTelemetry.MeterName));

builder.Services.AddDispatcherInfrastructure(
    builder.Configuration,
    builder.Environment);
builder.Services.AddDispatcherWorker(
    builder.Configuration);

await builder.Build().RunAsync();

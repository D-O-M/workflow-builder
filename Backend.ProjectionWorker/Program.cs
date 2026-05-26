using Backend.ProjectionWorker;
using Backend.Repositories.Processes;
using Backend.Topology;
using OpenTelemetry.Metrics;

AspireEndpointEnvironment.PreferAspireHttpPortsOverInheritedUrls();

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
WorkflowBuilderTopologyValidator.ValidateProjectionWorker(builder.Configuration);

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("WorkflowBuilder.Cosmos")
        .AddMeter(ProjectionWorkerTelemetry.MeterName)
        .AddMeter(ProcessSummaryProjectionTelemetry.MeterName)
        .AddMeter(ProcessRealtimeProjectionTelemetry.MeterName));

builder.Services.AddProjectionWorkerServices(
    builder.Configuration,
    builder.Environment);

await builder.Build().RunAsync();

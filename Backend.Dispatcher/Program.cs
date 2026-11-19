using Backend.Dispatcher;
using Backend.Repositories.Processes;
using Backend.Services.ProcessStepQueue;
using Backend.Topology;
using OpenTelemetry.Metrics;

AspireEndpointEnvironment.PreferAspireHttpPortsOverInheritedUrls();

const string recoveryArgument = "--resume-waiting-steps";
var runRecoveryOnce = args.Contains(recoveryArgument, StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(args.Where(argument => argument != recoveryArgument).ToArray());
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

using var host = builder.Build();
if (runRecoveryOnce)
{
    // Scheduled Container Apps jobs exit after this pass. They never start the long-lived worker.
    await using var scope = host.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<WaitingPluginStepRecovery>().RunAsync(CancellationToken.None);
}
else
{
    await host.RunAsync();
}

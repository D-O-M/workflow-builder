[CmdletBinding()]
param(
    [int]$SmokeSeconds = 60,
    [switch]$StartEngine
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repoRoot "IntegrationTests\WorkflowBuilder.IntegrationTests.AppHost\WorkflowBuilder.IntegrationTests.AppHost.csproj"
$liveRunsPath = Join-Path $repoRoot "IntegrationTests\WorkflowBuilder.IntegrationTests\LiveRuns"

New-Item -ItemType Directory -Force -Path $liveRunsPath | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stdoutPath = Join-Path $liveRunsPath "apphost-smoke-stdout-$timestamp.log"
$stderrPath = Join-Path $liveRunsPath "apphost-smoke-stderr-$timestamp.log"

$env:AzureCosmos__ResourceName = $env:AzureCosmos__ResourceName ?? "integration-cosmos"
$env:AzureCosmos__ConnectionName = $env:AzureCosmos__ConnectionName ?? "enginedb"
$env:AzureStorage__ResourceName = $env:AzureStorage__ResourceName ?? "integration-storage"
$env:AzureStorage__BlobContainers__Plugins__ResourceName = $env:AzureStorage__BlobContainers__Plugins__ResourceName ?? "plugins"
$env:AzureStorage__BlobContainers__Plugins__ContainerName = $env:AzureStorage__BlobContainers__Plugins__ContainerName ?? "plugins"
$env:AzureServiceBus__ResourceName = $env:AzureServiceBus__ResourceName ?? "integration-servicebus"
$env:AzureServiceBus__Queues__WorkflowTaskQueue__ResourceName = $env:AzureServiceBus__Queues__WorkflowTaskQueue__ResourceName ?? "workflow-task-queue"
$env:AzureServiceBus__Queues__WorkflowTaskQueue__ConnectionName = $env:AzureServiceBus__Queues__WorkflowTaskQueue__ConnectionName ?? "workflow-task-queue"
$env:AzureServiceBus__Queues__WorkflowTaskQueue__Name = $env:AzureServiceBus__Queues__WorkflowTaskQueue__Name ?? "workflowtasks"
$env:Parameters__SqlServerPassword = $env:Parameters__SqlServerPassword ?? "IntegrationTests_password_123!"
$env:SqlServerPassword = $env:SqlServerPassword ?? $env:Parameters__SqlServerPassword
$env:IntegrationTests__StartEngine = if ($StartEngine) { "true" } else { "false" }

$arguments = @(
    "run",
    "--project",
    $projectPath,
    "--no-launch-profile"
)

$process = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList $arguments `
    -WorkingDirectory $repoRoot `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath `
    -PassThru

Write-Host "Started AppHost smoke process $($process.Id)."
Write-Host "STDOUT: $stdoutPath"
Write-Host "STDERR: $stderrPath"

Start-Sleep -Seconds $SmokeSeconds

if ($process.HasExited)
{
    Write-Error "AppHost smoke exited early with code $($process.ExitCode). See logs above."
}

try
{
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}
catch
{
}

Write-Host "AppHost smoke stayed alive for $SmokeSeconds seconds."

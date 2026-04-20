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

function Set-EnvironmentDefault {
    param(
        [string]$Name,
        [string]$DefaultValue
    )

    $currentValue = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($currentValue))
    {
        [Environment]::SetEnvironmentVariable($Name, $DefaultValue)
    }
}

Set-EnvironmentDefault -Name "AzureCosmos__ResourceName" -DefaultValue "integration-cosmos"
Set-EnvironmentDefault -Name "AzureCosmos__ConnectionName" -DefaultValue "enginedb"
Set-EnvironmentDefault -Name "AzureStorage__ResourceName" -DefaultValue "integration-storage"
Set-EnvironmentDefault -Name "AzureStorage__BlobContainers__Plugins__ResourceName" -DefaultValue "plugins"
Set-EnvironmentDefault -Name "AzureStorage__BlobContainers__Plugins__ContainerName" -DefaultValue "plugins"
Set-EnvironmentDefault -Name "AzureServiceBus__ResourceName" -DefaultValue "integration-servicebus"
Set-EnvironmentDefault -Name "AzureServiceBus__Queues__ProcessStepQueue__ResourceName" -DefaultValue "process-step-queue"
Set-EnvironmentDefault -Name "AzureServiceBus__Queues__ProcessStepQueue__ConnectionName" -DefaultValue "process-step-queue"
Set-EnvironmentDefault -Name "AzureServiceBus__Queues__ProcessStepQueue__Name" -DefaultValue "processsteps"
Set-EnvironmentDefault -Name "Parameters__SqlServerPassword" -DefaultValue "IntegrationTests_password_123!"
Set-EnvironmentDefault -Name "SqlServerPassword" -DefaultValue ([Environment]::GetEnvironmentVariable("Parameters__SqlServerPassword"))
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

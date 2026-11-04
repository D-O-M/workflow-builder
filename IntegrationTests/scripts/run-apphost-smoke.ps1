[CmdletBinding()]
param(
    [int]$SmokeSeconds = 60,
    [switch]$StartEngine
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repoRoot "IntegrationTests\WorkflowBuilder.IntegrationTests.AppHost\WorkflowBuilder.IntegrationTests.AppHost.csproj"
$liveRunsPath = Join-Path $repoRoot "IntegrationTests\WorkflowBuilder.IntegrationTests\LiveRuns"
$topologyCatalogPath = Join-Path $repoRoot "BuildScripts\workflowbuilder-topology.json"
$topology = Get-Content -LiteralPath $topologyCatalogPath -Raw | ConvertFrom-Json

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
Set-EnvironmentDefault -Name "AzureCosmos__ConnectionName" -DefaultValue $topology.cosmos.defaultDatabaseName
Set-EnvironmentDefault -Name "AzureCosmos__DatabaseName" -DefaultValue $topology.cosmos.defaultDatabaseName
Set-EnvironmentDefault -Name "AzureStorage__ResourceName" -DefaultValue "integration-storage"
Set-EnvironmentDefault -Name "AzureStorage__BlobContainers__Plugins__ResourceName" -DefaultValue $topology.storage.plugins.resourceName
Set-EnvironmentDefault -Name "AzureStorage__BlobContainers__Plugins__ContainerName" -DefaultValue $topology.storage.plugins.containerName
Set-EnvironmentDefault -Name "AzureStorage__BlobContainers__Artifacts__ResourceName" -DefaultValue $topology.storage.artifacts.resourceName
Set-EnvironmentDefault -Name "AzureStorage__BlobContainers__Artifacts__ContainerName" -DefaultValue $topology.storage.artifacts.containerName
Set-EnvironmentDefault -Name "AzureServiceBus__ResourceName" -DefaultValue "integration-servicebus"
Set-EnvironmentDefault -Name "AzureServiceBus__Queues__ProcessStepQueue__ResourceName" -DefaultValue $topology.serviceBus.processStepQueue.resourceName
Set-EnvironmentDefault -Name "AzureServiceBus__Queues__ProcessStepQueue__ConnectionName" -DefaultValue $topology.serviceBus.processStepQueue.connectionName
Set-EnvironmentDefault -Name "AzureServiceBus__Queues__ProcessStepQueue__Name" -DefaultValue $topology.serviceBus.processStepQueue.queueName
Set-EnvironmentDefault -Name "AzureServiceBus__Queues__PluginControlQueue__ResourceName" -DefaultValue $topology.serviceBus.pluginControlQueue.resourceName
Set-EnvironmentDefault -Name "AzureServiceBus__Queues__PluginControlQueue__ConnectionName" -DefaultValue $topology.serviceBus.pluginControlQueue.connectionName
Set-EnvironmentDefault -Name "AzureServiceBus__Queues__PluginControlQueue__Name" -DefaultValue $topology.serviceBus.pluginControlQueue.queueName
$sqlPassword = [Environment]::GetEnvironmentVariable("Parameters__SqlServerPassword")
if ([string]::IsNullOrWhiteSpace($sqlPassword))
{
    $sqlPassword = [Environment]::GetEnvironmentVariable("SqlServerPassword")
}
if ([string]::IsNullOrWhiteSpace($sqlPassword))
{
    $sqlPassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) + "aA1!"
}
Set-EnvironmentDefault -Name "Parameters__SqlServerPassword" -DefaultValue $sqlPassword
Set-EnvironmentDefault -Name "SqlServerPassword" -DefaultValue $sqlPassword
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

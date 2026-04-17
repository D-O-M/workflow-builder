[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$Detached
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$solutionPath = Join-Path $repoRoot "Backend.slnx"
$projectPath = Join-Path $repoRoot "IntegrationTests\WorkflowBuilder.IntegrationTests\WorkflowBuilder.IntegrationTests.csproj"
$liveRunsPath = Join-Path $repoRoot "IntegrationTests\WorkflowBuilder.IntegrationTests\LiveRuns"

New-Item -ItemType Directory -Force -Path $liveRunsPath | Out-Null

function Stop-StaleIntegrationProcesses
{
    $processNames = @(
        "WorkflowBuilder.IntegrationTests",
        "IntegrationSeedPlugin",
        "IntegrationTransformPlugin",
        "IntegrationResultPlugin",
        "IntegrationFailurePlugin"
    )

    $staleProcesses = foreach ($processName in $processNames)
    {
        Get-Process -Name $processName -ErrorAction SilentlyContinue
    }

    $staleProcesses = $staleProcesses | Where-Object { $_ } | Sort-Object Id -Unique

    if ($staleProcesses.Count -eq 0) { return }

    Write-Host "Stopping stale integration processes: $($staleProcesses.Id -join ', ')"
    $staleProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

Stop-StaleIntegrationProcesses

if (-not $NoBuild)
{
    & dotnet build $solutionPath -v minimal
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }
}

$arguments = @(
    "test",
    "--project",
    $projectPath,
    "-v",
    "minimal",
    "--no-build"
)

$arguments += "--"
$arguments += "--maximum-parallel-tests"
$arguments += "1"
$arguments += "--output"
$arguments += "Detailed"

if ($Detached)
{
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $stdoutPath = Join-Path $liveRunsPath "stdout-$timestamp.log"
    $runnerStdoutPath = Join-Path $liveRunsPath "runner-stdout-$timestamp.log"
    $runnerStderrPath = Join-Path $liveRunsPath "runner-stderr-$timestamp.log"
    $escapedRepoRoot = $repoRoot.Replace("'", "''")
    $escapedStdoutPath = $stdoutPath.Replace("'", "''")
    $quotedArguments = $arguments | ForEach-Object { "'{0}'" -f $_.Replace("'", "''") }
    $argumentLiteral = "@(" + ($quotedArguments -join ", ") + ")"
    $detachedCommand = @"
Set-Location '$escapedRepoRoot'
`$arguments = $argumentLiteral
& dotnet @arguments 2>&1 | Tee-Object -FilePath '$escapedStdoutPath'
exit `$LASTEXITCODE
"@
    $process = Start-Process `
        -FilePath "pwsh" `
        -ArgumentList @("-NoLogo", "-NoProfile", "-Command", $detachedCommand) `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $runnerStdoutPath `
        -RedirectStandardError $runnerStderrPath `
        -PassThru

    Write-Host "Started detached integration run."
    Write-Host "PID: $($process.Id)"
    Write-Host "LOG: $stdoutPath"
    Write-Host "RUNNER STDOUT: $runnerStdoutPath"
    Write-Host "RUNNER STDERR: $runnerStderrPath"
    exit 0
}

& dotnet @arguments
exit $LASTEXITCODE

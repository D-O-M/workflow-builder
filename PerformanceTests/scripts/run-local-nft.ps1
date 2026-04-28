<#
.SYNOPSIS
  Runs a local WorkflowBuilder non-functional performance test proof.

.DESCRIPTION
  Builds deterministic NFT plugin bundles, starts the AppHost in a detached lean profile,
  seeds NFT blueprints/bundles, runs bounded load probes, collects metrics, writes a report,
  and stops the AppHost at the end.
#>

[CmdletBinding()]
param(
  [ValidateSet("Smoke", "Standard", "Staircase", "Capacity")]
  [string] $Profile = "Smoke",

  [ValidateSet("ceiling-lane", "product-lane", "both")]
  [string] $Lane = "ceiling-lane",

  [string] $NftBlueprintKey = "nft-ceiling-fanout-join",
  [switch] $IncludeObservability,
  [switch] $NoBuild,
  [switch] $BuildProductBundles,
  [switch] $KeepAppHost,
  [switch] $CompletionOnly,
  [int] $StartupTimeoutSeconds = 360,
  [int] $CeilingCompletionCount = 0,
  [int] $CeilingCompletionConcurrency = 0,
  [int] $CapacityStart = 20,
  [int] $CapacityMax = 320,
  [int] $CapacityTimeoutSeconds = 30,
  [int] $PluginRuntimeMaxConcurrentResultFinalizations = 80,
  [int] $ProcessStepQueueMaxConcurrentMessages = 32,
  [int] $ProcessStepQueuePrefetchCount = 128,
  [int] $ProcessStepQueueMaxAutoLockRenewalMinutes = 5
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path $repoRoot ".artifacts\nft\$timestamp"
$bundleRoot = Join-Path $runRoot "bundles"
$blueprintRoot = Join-Path $runRoot "blueprints"
$logRoot = Join-Path $runRoot "logs"
$apiBaseUrl = "http://localhost:5180"
$appHostPath = Join-Path $repoRoot "Backend.AppHost\Backend.AppHost.csproj"
$probeProjectPath = Join-Path $repoRoot "PerformanceTests\WorkflowBuilder.PerformanceProbe\WorkflowBuilder.PerformanceProbe.csproj"
$nftBlueprintKey = $NftBlueprintKey
$productBlueprintKey = "showcase-town-weekend-recommendation"
$scenarioFailuresPath = Join-Path $runRoot "scenario-failures.txt"

New-Item -ItemType Directory -Force -Path $runRoot, $bundleRoot, $blueprintRoot, $logRoot | Out-Null

function Write-Step([string] $Message) {
  Write-Host "[nft] $Message"
}

function Add-ScenarioFailure([string] $Message) {
  Add-Content -Path $scenarioFailuresPath -Value $Message
  Write-Warning $Message
}

function Invoke-CheckedCommand {
  param(
    [string] $FilePath,
    [string[]] $Arguments,
    [string] $WorkingDirectory = $repoRoot
  )

  Write-Step ("{0} {1}" -f $FilePath, ($Arguments -join " "))
  & $FilePath @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
  }
}

function Convert-ToSingleQuotedPowerShellLiteral([string] $Value) {
  return "'{0}'" -f $Value.Replace("'", "''")
}

function Set-EnvironmentDefault([string] $Name, [string] $Value) {
  [Environment]::SetEnvironmentVariable($Name, $Value)
}

function Split-BlueprintKeys([string] $BlueprintKeys) {
  return @($BlueprintKeys -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Ensure-DotnetCounters {
  if (Get-Command dotnet-counters -ErrorAction SilentlyContinue) {
    return $true
  }

  Write-Step "dotnet-counters is not installed; installing the free Microsoft diagnostics tool globally."
  & dotnet tool install -g dotnet-counters | Out-Host
  if ($LASTEXITCODE -ne 0) {
    Write-Warning "dotnet-counters install failed; the run will continue without runtime counter CSVs."
    return $false
  }

  $toolPath = Join-Path $env:USERPROFILE ".dotnet\tools"
  if ($env:PATH -notlike "*$toolPath*") {
    $env:PATH = "$env:PATH;$toolPath"
  }

  return [bool](Get-Command dotnet-counters -ErrorAction SilentlyContinue)
}

function Stop-StaleAspireAppHost {
  try {
    & aspire stop --apphost $appHostPath 2>$null | Out-Null
  }
  catch {
    Write-Verbose "No stale Aspire AppHost was stopped: $($_.Exception.Message)"
  }
}

function Stop-ProcessTree([int] $ProcessId) {
  $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue)
  foreach ($child in $children) {
    Stop-ProcessTree -ProcessId $child.ProcessId
  }

  try {
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
  }
  catch {
    Write-Verbose "Process $ProcessId was already stopped: $($_.Exception.Message)"
  }
}

function Test-ApiHealth {
  try {
    $response = Invoke-WebRequest -Uri "$apiBaseUrl/health" -UseBasicParsing -TimeoutSec 5
    return $response.StatusCode -ge 200 -and $response.StatusCode -lt 300
  }
  catch {
    return $false
  }
}

function Wait-ForApi {
  $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    if ($appHostProcess.HasExited) {
      throw "AppHost exited early with code $($appHostProcess.ExitCode). See $appHostStdout and $appHostStderr."
    }

    if (Test-ApiHealth) {
      return
    }

    Start-Sleep -Seconds 5
  }

  throw "Timed out waiting for API health at $apiBaseUrl/health."
}

function Wait-ForBlueprint([string] $BlueprintKey) {
  $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    try {
      $blueprints = Invoke-RestMethod -Uri "$apiBaseUrl/api/blueprints" -TimeoutSec 10
      if ($blueprints | Where-Object { $_.key -eq $BlueprintKey }) {
        return
      }
    }
    catch {
      Write-Verbose "Blueprint poll failed: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds 5
  }

  throw "Timed out waiting for blueprint '$BlueprintKey' to be seeded."
}

function Wait-ForTaskDefinitions([string[]] $TaskNames) {
  $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    try {
      $tasks = Invoke-RestMethod -Uri "$apiBaseUrl/Tasks" -TimeoutSec 10
      $names = @($tasks | ForEach-Object { $_.name })
      $missing = @($TaskNames | Where-Object { $names -notcontains $_ })
      if ($missing.Count -eq 0) {
        return
      }
    }
    catch {
      Write-Verbose "Task poll failed: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds 5
  }

  throw "Timed out waiting for task definitions: $($TaskNames -join ', ')."
}

function Copy-BlueprintSeeds {
  if ($Lane -eq "ceiling-lane" -or $Lane -eq "both") {
    Copy-Item -Path (Join-Path $repoRoot "PerformanceTests\Blueprints\*.json") -Destination $blueprintRoot -Force
  }

  if ($Lane -eq "product-lane" -or $Lane -eq "both") {
    Copy-Item -Path (Join-Path $repoRoot "SeedData\Blueprints\*.json") -Destination $blueprintRoot -Force
  }
}

function Build-NftBundles {
  Invoke-CheckedCommand -FilePath "pwsh" -Arguments @(
    "-NoLogo",
    "-NoProfile",
    "-File",
    (Join-Path $repoRoot "BuildScripts\bundle-plugins.ps1"),
    "-RootDir",
    (Join-Path $repoRoot "PerformanceTests\Plugins"),
    "-OutputDir",
    $bundleRoot,
    "-Configuration",
    "Release",
    "-RuntimeIdentifier",
    "win-x64",
    "-Overwrite",
    "-Minify"
  )
}

function Copy-ProductBundles {
  $productionBundleRoot = Join-Path $repoRoot "Backend.Plugins\_bundles"
  if ($BuildProductBundles) {
    Invoke-CheckedCommand -FilePath "pwsh" -Arguments @(
      "-NoLogo",
      "-NoProfile",
      "-File",
      (Join-Path $repoRoot "BuildScripts\bundle-plugins.ps1"),
      "-RootDir",
      (Join-Path $repoRoot "Backend.Plugins"),
      "-OutputDir",
      $productionBundleRoot,
      "-Configuration",
      "Release",
      "-RuntimeIdentifier",
      "win-x64",
      "-Overwrite",
      "-Minify"
    )
  }

  $bundles = @(Get-ChildItem -LiteralPath $productionBundleRoot -Filter *.zip -File -ErrorAction SilentlyContinue)
  if ($bundles.Count -eq 0) {
    throw "Product lane requested, but no production bundles were found in $productionBundleRoot. Re-run with -BuildProductBundles."
  }

  $bundles | Copy-Item -Destination $bundleRoot -Force
}

function Start-AppHost {
  $appHostStdout = Join-Path $logRoot "apphost-stdout.log"
  $appHostStderr = Join-Path $logRoot "apphost-stderr.log"
  $envLines = @{
    "ASPNETCORE_ENVIRONMENT" = "Development"
    "DOTNET_ENVIRONMENT" = "Development"
    "ASPIRE_ALLOW_UNSECURED_TRANSPORT" = "true"
    "ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS" = "true"
    "Frontend__Enabled" = "false"
    "Observability__Enabled" = if ($IncludeObservability) { "true" } else { "false" }
    "Nft__Enabled" = "true"
    "Nft__PluginRuntime__MaxConcurrentResultFinalizations" = $PluginRuntimeMaxConcurrentResultFinalizations.ToString()
    "Nft__ProcessStepQueue__MaxConcurrentMessages" = $ProcessStepQueueMaxConcurrentMessages.ToString()
    "Nft__ProcessStepQueue__PrefetchCount" = $ProcessStepQueuePrefetchCount.ToString()
    "Nft__ProcessStepQueue__MaxAutoLockRenewalMinutes" = $ProcessStepQueueMaxAutoLockRenewalMinutes.ToString()
    "PluginSeeding__Enabled" = "true"
    "PluginSeeding__BundlesRoot" = $bundleRoot
    "BlueprintSeeding__Enabled" = "true"
    "BlueprintSeeding__BlueprintsRoot" = $blueprintRoot
    "AzureCosmos__PersistData" = "false"
    "AzureStorage__PersistData" = "false"
    "SqlServer__PersistData" = "false"
    "AzureServiceBus__Queues__PluginControlQueue__ResourceName" = "plugin-control-queue"
    "AzureServiceBus__Queues__PluginControlQueue__ConnectionName" = "plugin-control-queue"
    "AzureServiceBus__Queues__PluginControlQueue__Name" = "pluginreloadqueue"
  }.GetEnumerator() | ForEach-Object {
    "`$env:$($_.Key) = $(Convert-ToSingleQuotedPowerShellLiteral $_.Value)"
  }

  $command = @"
Set-Location $(Convert-ToSingleQuotedPowerShellLiteral $repoRoot)
$($envLines -join [Environment]::NewLine)
dotnet run --project $(Convert-ToSingleQuotedPowerShellLiteral $appHostPath) --launch-profile nft -c Release --no-build
exit `$LASTEXITCODE
"@

  $process = Start-Process `
    -FilePath "pwsh" `
    -ArgumentList @("-NoLogo", "-NoProfile", "-Command", $command) `
    -WorkingDirectory $repoRoot `
    -RedirectStandardOutput $appHostStdout `
    -RedirectStandardError $appHostStderr `
    -WindowStyle Hidden `
    -PassThru

  return [pscustomobject]@{
    Process = $process
    Stdout = $appHostStdout
    Stderr = $appHostStderr
  }
}

function Start-DockerStatsCapture {
  if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    return $null
  }

  $outputPath = Join-Path $runRoot "docker-stats.csv"
  "timestampUtc,name,cpuPercent,memUsage,memPercent,netIo,blockIo,pids" | Set-Content -LiteralPath $outputPath -Encoding UTF8
  $command = @"
while (`$true) {
  `$timestamp = (Get-Date).ToUniversalTime().ToString('o')
  docker stats --no-stream --format "{{.Name}},{{.CPUPerc}},{{.MemUsage}},{{.MemPerc}},{{.NetIO}},{{.BlockIO}},{{.PIDs}}" 2>`$null |
    ForEach-Object { "`$timestamp,`$_" } |
    Add-Content -LiteralPath $(Convert-ToSingleQuotedPowerShellLiteral $outputPath)
  Start-Sleep -Seconds 5
}
"@

  return Start-Process `
    -FilePath "pwsh" `
    -ArgumentList @("-NoLogo", "-NoProfile", "-Command", $command) `
    -WorkingDirectory $repoRoot `
    -WindowStyle Hidden `
    -PassThru
}

function Find-DotnetProcessId([string] $ProjectName) {
  $match = Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -match [regex]::Escape($ProjectName) } |
    Sort-Object CreationDate -Descending |
    Select-Object -First 1

  return $match.ProcessId
}

function Start-CounterCapture([string] $ProjectName, [string] $OutputName) {
  if (-not (Get-Command dotnet-counters -ErrorAction SilentlyContinue)) {
    return $null
  }

  $processId = Find-DotnetProcessId $ProjectName
  if (-not $processId) {
    Write-Warning "Could not locate a process for $ProjectName; skipping dotnet-counters capture."
    return $null
  }

  $outputPath = Join-Path $runRoot $OutputName
  return Start-Process `
    -FilePath "dotnet-counters" `
    -ArgumentList @(
      "collect",
      "--process-id",
      $processId,
      "--refresh-interval",
      "3",
      "--format",
      "csv",
      "--output",
      $outputPath,
      "--counters",
      "System.Runtime,Microsoft.AspNetCore.Hosting,Microsoft.AspNetCore.Server.Kestrel,WorkflowBuilder.Engine.Processes,WorkflowBuilder.API.ProcessStart,WorkflowBuilder.Cosmos"
    ) `
    -WorkingDirectory $repoRoot `
    -WindowStyle Hidden `
    -PassThru
}

function Convert-ToContainerPath([string] $Path) {
  $absolutePath = [System.IO.Path]::GetFullPath($Path)
  $relative = [System.IO.Path]::GetRelativePath($repoRoot, $absolutePath).Replace("\", "/")
  return "/repo/$relative"
}

function Invoke-K6Script {
  param(
    [string] $ScriptName,
    [string] $SummaryName,
    [string] $BlueprintKey,
    [string] $PayloadPath = ""
  )

  $scriptPath = Join-Path $repoRoot "PerformanceTests\k6\$ScriptName"
  $summaryPath = Join-Path $runRoot $SummaryName
  $logPath = Join-Path $logRoot "$ScriptName.log"
  $envValues = @{
    "API_BASE_URL" = $apiBaseUrl
    "BLUEPRINT_KEY" = $BlueprintKey
    "NFT_PROFILE" = $Profile.ToLowerInvariant()
    "K6_SUMMARY_PATH" = $summaryPath
  }

  if ($PayloadPath) {
    $envValues["PROCESS_PAYLOAD_JSON"] = $PayloadPath
  }

  if (Get-Command k6 -ErrorAction SilentlyContinue) {
    $previous = @{}
    foreach ($entry in $envValues.GetEnumerator()) {
      $previous[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key)
      [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }

    try {
      & k6 run $scriptPath 2>&1 | Tee-Object -FilePath $logPath
      if ($LASTEXITCODE -ne 0) {
        Add-ScenarioFailure "k6 script $ScriptName crossed thresholds or failed with exit code $LASTEXITCODE."
      }
    }
    finally {
      foreach ($entry in $previous.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
      }
    }

    return
  }

  if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Neither local k6 nor Docker is available. Install k6 with winget/choco, or enable Docker for the grafana/k6 fallback."
  }

  $containerEnv = @{
    "API_BASE_URL" = "http://host.docker.internal:5180"
    "BLUEPRINT_KEY" = $BlueprintKey
    "NFT_PROFILE" = $Profile.ToLowerInvariant()
    "K6_SUMMARY_PATH" = (Convert-ToContainerPath $summaryPath)
  }

  if ($PayloadPath) {
    $containerEnv["PROCESS_PAYLOAD_JSON"] = (Convert-ToContainerPath $PayloadPath)
  }

  $dockerArgs = @("run", "--rm")
  foreach ($entry in $containerEnv.GetEnumerator()) {
    $dockerArgs += @("-e", "$($entry.Key)=$($entry.Value)")
  }
  $dockerArgs += @("-v", "${repoRoot}:/repo", "grafana/k6:latest", "run", (Convert-ToContainerPath $scriptPath))

  & docker @dockerArgs 2>&1 | Tee-Object -FilePath $logPath
  if ($LASTEXITCODE -ne 0) {
    Add-ScenarioFailure "Docker k6 script $ScriptName crossed thresholds or failed with exit code $LASTEXITCODE."
  }
}

function Invoke-PerformanceProbe {
  param(
    [string] $Command,
    [string] $OutputName,
    [string] $BlueprintKey,
    [int] $Count,
    [int] $Concurrency = 1,
    [int] $TimeoutSeconds = 60,
    [string] $PayloadPath = ""
  )

  $arguments = @(
    "run",
    "--project",
    $probeProjectPath,
    "-c",
    "Release",
    "--no-build",
    "--",
    $Command,
    "--api-base-url",
    $apiBaseUrl,
    "--output",
    (Join-Path $runRoot $OutputName),
    "--count",
    $Count.ToString(),
    "--concurrency",
    $Concurrency.ToString(),
    "--timeout-seconds",
    $TimeoutSeconds.ToString()
  )

  if ((Split-BlueprintKeys $BlueprintKey).Count -gt 1) {
    $arguments += @("--blueprint-keys", $BlueprintKey)
  }
  else {
    $arguments += @("--blueprint-key", $BlueprintKey)
  }

  if ($PayloadPath) {
    $arguments += @("--payload", $PayloadPath)
  }

  Invoke-CheckedCommand -FilePath "dotnet" -Arguments $arguments
}

function Write-RunConfig {
  $config = [ordered]@{
    timestamp = $timestamp
    profile = $Profile
    lane = $Lane
    nftBlueprintKey = $nftBlueprintKey
    includeObservability = [bool] $IncludeObservability
    apiBaseUrl = $apiBaseUrl
    gitSha = (& git rev-parse HEAD)
    dotnet = (& dotnet --version)
    docker = if (Get-Command docker -ErrorAction SilentlyContinue) { (& docker --version) } else { "<missing>" }
    k6 = if (Get-Command k6 -ErrorAction SilentlyContinue) { (& k6 version) } else { "docker fallback or missing" }
    processor = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)
    logicalProcessors = (Get-CimInstance Win32_Processor | Measure-Object NumberOfLogicalProcessors -Sum).Sum
    memoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    completionOnly = [bool] $CompletionOnly
    ceilingCompletionCount = $CeilingCompletionCount
    ceilingCompletionConcurrency = $CeilingCompletionConcurrency
    capacityStart = $CapacityStart
    capacityMax = $CapacityMax
    capacityTimeoutSeconds = $CapacityTimeoutSeconds
    pluginRuntimeMaxConcurrentResultFinalizations = $PluginRuntimeMaxConcurrentResultFinalizations
    processStepQueueMaxConcurrentMessages = $ProcessStepQueueMaxConcurrentMessages
    processStepQueuePrefetchCount = $ProcessStepQueuePrefetchCount
    processStepQueueMaxAutoLockRenewalMinutes = $ProcessStepQueueMaxAutoLockRenewalMinutes
  }

  $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runRoot "run-config.json") -Encoding UTF8
}

function Get-PercentileValue([double[]] $Values, [double] $Percentile) {
  if ($Values.Count -eq 0) {
    return $null
  }

  $ordered = @($Values | Sort-Object)
  $index = [Math]::Ceiling($Percentile * $ordered.Count) - 1
  $index = [Math]::Max(0, [Math]::Min($ordered.Count - 1, $index))
  return [double] $ordered[$index]
}

function Format-MetricValue($Value, [string] $Suffix = "ms") {
  if ($null -eq $Value -or ($Value -is [double] -and [double]::IsNaN($Value))) {
    return "n/a"
  }

  return "{0:n1}{1}" -f ([double] $Value), $Suffix
}

function Get-CsvP95([string] $FileName, [string] $ColumnName) {
  $path = Join-Path $runRoot $FileName
  if (-not (Test-Path -LiteralPath $path)) {
    return $null
  }

  $values = @(Import-Csv -LiteralPath $path |
    ForEach-Object {
      $raw = $_.$ColumnName
      if (-not [string]::IsNullOrWhiteSpace($raw)) {
        [double] $raw
      }
    })

  return Get-PercentileValue -Values $values -Percentile 0.95
}

function Get-K6MetricValue([string] $FileName, [string] $MetricName, [string] $ValueName) {
  $path = Join-Path $runRoot $FileName
  if (-not (Test-Path -LiteralPath $path)) {
    return $null
  }

  $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
  $metric = $json.metrics.$MetricName
  if ($null -eq $metric) {
    return $null
  }

  if ($metric.PSObject.Properties.Name -contains "values") {
    return $metric.values.$ValueName
  }

  return $null
}

function Get-BlueprintId([string] $BlueprintKey) {
  $blueprints = Invoke-RestMethod -Uri "$apiBaseUrl/api/blueprints" -TimeoutSec 10
  $match = @($blueprints | Where-Object { $_.key -eq $BlueprintKey }) | Select-Object -First 1
  if (-not $match) {
    throw "Blueprint '$BlueprintKey' was not found."
  }

  return [int] $match.id
}

function Get-BlueprintIdMap([string] $BlueprintKeys) {
  $blueprints = Invoke-RestMethod -Uri "$apiBaseUrl/api/blueprints" -TimeoutSec 10
  $map = @{}
  foreach ($key in (Split-BlueprintKeys $BlueprintKeys)) {
    $match = @($blueprints | Where-Object { $_.key -eq $key }) | Select-Object -First 1
    if (-not $match) {
      throw "Blueprint '$key' was not found."
    }

    $map[$key] = [int] $match.id
  }

  return $map
}

function Test-ProcessTerminal([int] $BlueprintId, [string] $ProcessId) {
  try {
    $detail = Invoke-RestMethod -Uri "$apiBaseUrl/api/blueprints/$BlueprintId/processes/$ProcessId" -TimeoutSec 10
    return ($detail.status -eq "Completed" -or $detail.status -eq "Failed")
  }
  catch {
    return $false
  }
}

function Wait-ForCapacityProcessesToDrain(
  [string] $CsvPath,
  [hashtable] $BlueprintIds,
  [int] $TimeoutSeconds = 180) {
  if (-not (Test-Path -LiteralPath $CsvPath)) {
    return
  }

  $defaultBlueprintId = @($BlueprintIds.Values | Select-Object -First 1)[0]
  $processes = @(Import-Csv -LiteralPath $CsvPath |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_.processId) } |
    ForEach-Object {
      $key = $_.blueprintKey
      $blueprintId = if (-not [string]::IsNullOrWhiteSpace($key) -and $BlueprintIds.ContainsKey($key)) {
        [int] $BlueprintIds[$key]
      }
      else {
        [int] $defaultBlueprintId
      }

      [pscustomobject]@{
        processId = $_.processId
        blueprintId = $blueprintId
      }
    })
  if ($processes.Count -eq 0) {
    return
  }

  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    $pending = @($processes | Where-Object { -not (Test-ProcessTerminal -BlueprintId $_.blueprintId -ProcessId $_.processId) })
    if ($pending.Count -eq 0) {
      return
    }

    Start-Sleep -Seconds 2
  } while ((Get-Date) -lt $deadline)

  Add-ScenarioFailure "Capacity sweep left $($pending.Count) process(es) non-terminal after waiting $TimeoutSeconds seconds."
}

function Get-CapacityProbeResult(
  [int] $Concurrency,
  [string] $CsvPath,
  [int] $TimeoutSeconds) {
  $rows = @(Import-Csv -LiteralPath $CsvPath)
  $latencies = @($rows | ForEach-Object { [double] $_.latencyMs })
  $maxLatency = if ($latencies.Count -gt 0) { ($latencies | Measure-Object -Maximum).Maximum } else { [double]::NaN }
  $p95 = Get-PercentileValue -Values $latencies -Percentile 0.95
  $completedRows = @($rows | Where-Object { $_.status -eq "Completed" })
  $completedProcesses = $completedRows.Count
  $completedTasks = @($completedRows | ForEach-Object { [int] $_.stepCount } | Measure-Object -Sum).Sum
  $timeoutMs = $TimeoutSeconds * 1000
  $completedWithinTimeoutRows = @($completedRows | Where-Object { [double] $_.latencyMs -le $timeoutMs })
  $completedProcessesWithinTimeout = $completedWithinTimeoutRows.Count
  $completedTasksWithinTimeout = @($completedWithinTimeoutRows | ForEach-Object { [int] $_.stepCount } | Measure-Object -Sum).Sum
  $timedOutProcesses = @($rows | Where-Object { $_.status -eq "Timeout" }).Count
  $failedProcesses = $rows.Count - $completedProcesses - $timedOutProcesses
  $passed = $rows.Count -eq $Concurrency -and
    $completedProcessesWithinTimeout -eq $Concurrency

  [pscustomobject]@{
    concurrency = $Concurrency
    totalProcesses = $rows.Count
    completedProcesses = $completedProcesses
    completedTasks = [int] $completedTasks
    completedProcessesWithinTimeout = $completedProcessesWithinTimeout
    completedTasksWithinTimeout = [int] $completedTasksWithinTimeout
    timedOutProcesses = $timedOutProcesses
    failedProcesses = $failedProcesses
    p95Ms = [Math]::Round([double] $p95, 3)
    maxMs = [Math]::Round([double] $maxLatency, 3)
    timeoutSeconds = $TimeoutSeconds
    passed = [bool] $passed
    csv = $CsvPath
  }
}

function Invoke-CapacityCandidate(
  [int] $Concurrency,
  [hashtable] $BlueprintIds,
  [int] $TimeoutSeconds) {
  $outputName = "capacity-processes-$Concurrency.csv"
  $csvPath = Join-Path $runRoot $outputName

  Write-Step "Capacity candidate: $Concurrency concurrent process(es), strict timeout ${TimeoutSeconds}s."
  Invoke-PerformanceProbe `
    -Command "completion" `
    -OutputName $outputName `
    -BlueprintKey $nftBlueprintKey `
    -Count $Concurrency `
    -Concurrency $Concurrency `
    -TimeoutSeconds $TimeoutSeconds |
    ForEach-Object { Write-Host $_ }

  $result = Get-CapacityProbeResult -Concurrency $Concurrency -CsvPath $csvPath -TimeoutSeconds $TimeoutSeconds
  Wait-ForCapacityProcessesToDrain -CsvPath $csvPath -BlueprintIds $BlueprintIds

  Write-Step ("Capacity candidate {0}: pass={1}, completed={2}/{3}, tasks={4}, p95={5:n1}ms, max={6:n1}ms" -f `
      $Concurrency,
      $result.passed,
      $result.completedProcesses,
      $result.totalProcesses,
      $result.completedTasks,
      $result.p95Ms,
      $result.maxMs)

  return $result
}

function Invoke-CapacityDiscovery {
  $blueprintIds = Get-BlueprintIdMap $nftBlueprintKey
  $results = [System.Collections.Generic.List[object]]::new()
  $candidate = [Math]::Max(1, $CapacityStart)
  $maxCandidate = [Math]::Max($candidate, $CapacityMax)
  $lastPass = $null
  $firstFail = $null

  while ($candidate -le $maxCandidate) {
    $result = Invoke-CapacityCandidate `
      -Concurrency $candidate `
      -BlueprintIds $blueprintIds `
      -TimeoutSeconds $CapacityTimeoutSeconds
    $results.Add($result)

    if ($result.passed) {
      $lastPass = $result
      if ($candidate -eq $maxCandidate) {
        break
      }

      $candidate = [Math]::Min($candidate * 2, $maxCandidate)
      continue
    }

    $firstFail = $result
    break
  }

  if ($firstFail -ne $null) {
    $low = if ($lastPass -ne $null) { [int] $lastPass.concurrency + 1 } else { 1 }
    $high = [int] $firstFail.concurrency - 1

    while ($low -le $high) {
      $mid = [int] [Math]::Floor(($low + $high) / 2)
      if ($results | Where-Object { $_.concurrency -eq $mid } | Select-Object -First 1) {
        break
      }

      $result = Invoke-CapacityCandidate `
        -Concurrency $mid `
        -BlueprintIds $blueprintIds `
        -TimeoutSeconds $CapacityTimeoutSeconds
      $results.Add($result)

      if ($result.passed) {
        $lastPass = $result
        $low = $mid + 1
      }
      else {
        $firstFail = $result
        $high = $mid - 1
      }
    }
  }

  $orderedResults = @($results | Sort-Object concurrency)
  $orderedResults |
    Select-Object concurrency,totalProcesses,completedProcesses,completedTasks,completedProcessesWithinTimeout,completedTasksWithinTimeout,timedOutProcesses,failedProcesses,p95Ms,maxMs,timeoutSeconds,passed,csv |
    Export-Csv -LiteralPath (Join-Path $runRoot "capacity-summary.csv") -NoTypeInformation

  $best = @($orderedResults | Where-Object passed | Sort-Object concurrency -Descending | Select-Object -First 1)
  $capacitySummary = [ordered]@{
    timeoutSeconds = $CapacityTimeoutSeconds
    maxConfiguredConcurrency = $CapacityMax
    maxPassingProcesses = if ($best.Count -gt 0) { [int] $best[0].concurrency } else { 0 }
    maxPassingTasks = if ($best.Count -gt 0) { [int] $best[0].completedTasksWithinTimeout } else { 0 }
    candidates = $orderedResults
  }

  $capacitySummary |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $runRoot "capacity-summary.json") -Encoding UTF8

  if ($best.Count -gt 0) {
    Write-Step "Capacity result: $($best[0].concurrency) concurrent processes / $($best[0].completedTasksWithinTimeout) completed tasks inside ${CapacityTimeoutSeconds}s."
  }
  else {
    Add-ScenarioFailure "No capacity candidate completed fully inside ${CapacityTimeoutSeconds}s."
  }
}

function Write-Summary {
  $summaryPath = Join-Path $runRoot "summary.md"
  $bundleCount = @(Get-ChildItem -LiteralPath $bundleRoot -Filter *.zip -File).Count
  $blueprintCount = @(Get-ChildItem -LiteralPath $blueprintRoot -Filter *.json -File).Count
  $runConfig = Get-Content -LiteralPath (Join-Path $runRoot "run-config.json") -Raw | ConvertFrom-Json
  $bundleNames = @(Get-ChildItem -LiteralPath $bundleRoot -Filter *.zip -File |
    Sort-Object Name |
    ForEach-Object { "- $($_.BaseName) ({0:n2} MiB)" -f ($_.Length / 1MB) })

  $completionP95 = Get-CsvP95 "process-latencies-ceiling.csv" "latencyMs"
  $realtimeAcceptedP95 = Get-CsvP95 "realtime-latencies-ceiling.csv" "acceptedToEventMs"
  $realtimeUpdateP95 = Get-CsvP95 "realtime-latencies-ceiling.csv" "updateToEventMs"
  $admissionP95 = Get-K6MetricValue "k6-summary-admission-ceiling.json" "http_req_duration" "p(95)"
  $admissionFailureRate = Get-K6MetricValue "k6-summary-admission-ceiling.json" "http_req_failed" "rate"
  $k6CompletionP95 = Get-K6MetricValue "k6-summary-completion-ceiling.json" "process_completion_latency" "p(95)"
  $k6CompletionFailureRate = Get-K6MetricValue "k6-summary-completion-ceiling.json" "process_completion_failed" "rate"
  $searchP95 = Get-K6MetricValue "k6-summary-search-ceiling.json" "http_req_duration" "p(95)"
  $searchFailureRate = Get-K6MetricValue "k6-summary-search-ceiling.json" "http_req_failed" "rate"
  $completionRows = @()
  $completionPath = Join-Path $runRoot "process-latencies-ceiling.csv"
  if (Test-Path -LiteralPath $completionPath) {
    $completionRows = @(Import-Csv -LiteralPath $completionPath)
  }
  $capacitySummaryPath = Join-Path $runRoot "capacity-summary.json"
  $capacitySummary = $null
  if (Test-Path -LiteralPath $capacitySummaryPath) {
    $capacitySummary = Get-Content -LiteralPath $capacitySummaryPath -Raw | ConvertFrom-Json
  }
  $completionTotal = $completionRows.Count
  $completionSucceeded = @($completionRows | Where-Object { $_.status -eq "Completed" }).Count
  $completionSuccessRate = if ($completionTotal -gt 0) { ($completionSucceeded / $completionTotal) * 100 } else { $null }
  $realtimeRows = @()
  $realtimePath = Join-Path $runRoot "realtime-latencies-ceiling.csv"
  if (Test-Path -LiteralPath $realtimePath) {
    $realtimeRows = @(Import-Csv -LiteralPath $realtimePath)
  }
  $realtimeTimeouts = @($realtimeRows | Where-Object { $_.status -like "timeout:*" }).Count

  $notes = [System.Collections.Generic.List[string]]::new()
  if ($completionSuccessRate -ne $null -and $completionSuccessRate -lt 99) {
    $notes.Add("- Process completion success rate was below the default 99% gate.")
  }
  if ($completionP95 -ne $null -and $completionP95 -gt 30000) {
    $notes.Add("- Completion P95 exceeded the default 30s deterministic Blueprint SLA.")
  }
  if ($k6CompletionP95 -ne $null -and $k6CompletionP95 -gt 30000) {
    $notes.Add("- k6 completion P95 exceeded the default 30s deterministic Blueprint SLA.")
  }
  if ($searchP95 -ne $null -and $searchP95 -gt 750) {
    $notes.Add("- Search P95 exceeded the default 750ms bounded-query SLA.")
  }
  if ($realtimeUpdateP95 -ne $null -and $realtimeUpdateP95 -gt 2000) {
    $notes.Add("- Realtime update-to-event P95 exceeded the default 2s local realtime SLA; inspect broadcaster polling and SignalR delivery.")
  }
  if ($capacitySummary -ne $null -and [int]$capacitySummary.maxPassingProcesses -lt [int]$capacitySummary.maxConfiguredConcurrency) {
    $notes.Add("- Capacity sweep found the 30s process limit before the configured maximum of $($capacitySummary.maxConfiguredConcurrency).")
  }
  if (Test-Path $scenarioFailuresPath) {
    foreach ($failure in Get-Content $scenarioFailuresPath) {
      if (-not [string]::IsNullOrWhiteSpace($failure)) {
        $notes.Add("- $failure")
      }
    }
  }
  if ($notes.Count -eq 0) {
    $notes.Add("- No obvious smoke-profile threshold breach was detected in the collected artifacts.")
  }

  $summary = @(
    "# WorkflowBuilder Local NFT Run",
    "",
    "- Timestamp: $timestamp",
    "- Profile: $Profile",
    "- Lane: $Lane",
    "- NFT blueprint key: $($runConfig.nftBlueprintKey)",
    "- Observability included: $([bool]$IncludeObservability)",
    "- Plugin runtime max concurrent result finalizations: $($runConfig.pluginRuntimeMaxConcurrentResultFinalizations)",
    "- Process-step queue max concurrent messages: $($runConfig.processStepQueueMaxConcurrentMessages)",
    "- Process-step queue prefetch count: $($runConfig.processStepQueuePrefetchCount)",
    "- Process-step queue max auto-lock renewal: $($runConfig.processStepQueueMaxAutoLockRenewalMinutes) minute(s)",
    "- API base URL: $apiBaseUrl",
    "- Bundles staged: $bundleCount",
    "- Blueprints staged: $blueprintCount",
    "- Git SHA: $($runConfig.gitSha)",
    "- .NET SDK: $($runConfig.dotnet)",
    "- Processor: $($runConfig.processor)",
    "- Logical processors: $($runConfig.logicalProcessors)",
    "- Memory: $([Math]::Round([double]$runConfig.memoryBytes / 1GB, 2)) GiB",
    "- AppHost stdout: $appHostStdout",
    "- AppHost stderr: $appHostStderr",
    "",
    "## Gates",
    "",
    "- API request failure rate: < 1%",
    "- Process completion success: >= 99%",
    "- Deterministic completion P95: < 30s",
    "- Bounded recent search P95: < 750ms",
    "- Realtime update-to-event P95: < 2s",
    "",
    "## Results",
    "",
    "- Completion success: $completionSucceeded/$completionTotal ($(Format-MetricValue $completionSuccessRate '%'))",
    "- Completion P95: $(Format-MetricValue $completionP95)",
    "- k6 completion P95: $(Format-MetricValue $k6CompletionP95); failure rate: $(Format-MetricValue $k6CompletionFailureRate '')",
    "- Realtime timeouts: $realtimeTimeouts/$($realtimeRows.Count)",
    "- Realtime accepted-to-event P95: $(Format-MetricValue $realtimeAcceptedP95)",
    "- Realtime update-to-event P95: $(Format-MetricValue $realtimeUpdateP95)",
    "- Admission HTTP P95: $(Format-MetricValue $admissionP95); failure rate: $(Format-MetricValue $admissionFailureRate '')",
    "- Search HTTP P95: $(Format-MetricValue $searchP95); failure rate: $(Format-MetricValue $searchFailureRate '')",
    "",
    "## Capacity",
    "",
    "- Strict timeout: $(if ($capacitySummary -ne $null) { "$($capacitySummary.timeoutSeconds)s" } else { 'n/a' })",
    "- Max passing concurrent processes: $(if ($capacitySummary -ne $null) { $capacitySummary.maxPassingProcesses } else { 'n/a' })",
    "- Max completed tasks in passing batch: $(if ($capacitySummary -ne $null) { $capacitySummary.maxPassingTasks } else { 'n/a' })",
    "- Capacity candidates: $(if ($capacitySummary -ne $null) { 'capacity-summary.csv' } else { 'n/a' })",
    "",
    "## Bundle Versions",
    ""
  ) + $bundleNames + @(
    "",
    "## Notes",
    ""
  ) + $notes + @(
    "",
    "## Artifacts",
    "",
    "- k6 summaries: ``k6-summary-*.json``",
    "- Process latency CSVs: ``process-latencies*.csv``",
    "- Realtime latency CSVs: ``realtime-latencies*.csv``",
    "- Runtime counters: ``dotnet-counters-*.csv`` when available",
    "- Docker stats: ``docker-stats.csv`` when Docker is available",
    ""
  )

  $summary | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

$appHostProcess = $null
$dockerStatsProcess = $null
$counterProcesses = @()
$appHostStdout = Join-Path $logRoot "apphost-stdout.log"
$appHostStderr = Join-Path $logRoot "apphost-stderr.log"

try {
  Write-RunConfig
  Stop-StaleAspireAppHost

  if (Test-ApiHealth) {
    throw "Backend.API is already responding on $apiBaseUrl. Stop the existing app before running the NFT proof."
  }

  if (-not $NoBuild) {
    Invoke-CheckedCommand -FilePath "dotnet" -Arguments @("build", (Join-Path $repoRoot "Backend.API\Backend.API.csproj"), "-c", "Release")
    Invoke-CheckedCommand -FilePath "dotnet" -Arguments @("build", (Join-Path $repoRoot "Backend.Engine\Backend.Engine.csproj"), "-c", "Release")
    Invoke-CheckedCommand -FilePath "dotnet" -Arguments @("build", $probeProjectPath, "-c", "Release")
    Invoke-CheckedCommand -FilePath "dotnet" -Arguments @("build", $appHostPath, "-c", "Release")
  }

  Copy-BlueprintSeeds
  Build-NftBundles
  if ($Lane -eq "product-lane" -or $Lane -eq "both") {
    Copy-ProductBundles
  }

  $countersAvailable = Ensure-DotnetCounters
  $dockerStatsProcess = Start-DockerStatsCapture

  $appHost = Start-AppHost
  $appHostProcess = $appHost.Process
  $appHostStdout = $appHost.Stdout
  $appHostStderr = $appHost.Stderr

  Write-Step "Waiting for API health and seeded metadata."
  Wait-ForApi
  Wait-ForTaskDefinitions @(
    "NftSeedPlugin",
    "NftBranchAlphaPlugin",
    "NftBranchBetaPlugin",
    "NftBranchGammaPlugin",
    "NftJoinPlugin",
    "NftSinkPlugin",
    "NftLinearLaneOnePlugin",
    "NftLinearLaneTwoPlugin",
    "NftLinearLaneThreePlugin",
    "NftLinearLaneFourPlugin",
    "NftLinearLaneFivePlugin"
  )
  if ($Lane -eq "ceiling-lane" -or $Lane -eq "both") {
    foreach ($blueprintKey in (Split-BlueprintKeys $nftBlueprintKey)) {
      Wait-ForBlueprint $blueprintKey
    }
  }
  if ($Lane -eq "product-lane" -or $Lane -eq "both") {
    Wait-ForBlueprint $productBlueprintKey
  }

  if ($countersAvailable) {
    $counterProcesses += Start-CounterCapture -ProjectName "Backend.API" -OutputName "dotnet-counters-api.csv"
    $counterProcesses += Start-CounterCapture -ProjectName "Backend.Engine" -OutputName "dotnet-counters-engine.csv"
  }

  if ($Lane -eq "ceiling-lane" -or $Lane -eq "both") {
    $defaultCompletionCount = if ($Profile -eq "Smoke") { 3 } else { 50 }
    $defaultCompletionConcurrency = if ($Profile -eq "Smoke") { 1 } else { 10 }
    $completionCount = if ($CeilingCompletionCount -gt 0) { $CeilingCompletionCount } else { $defaultCompletionCount }
    $completionConcurrency = if ($CeilingCompletionConcurrency -gt 0) { $CeilingCompletionConcurrency } else { $defaultCompletionConcurrency }

    if ($Profile -eq "Smoke") {
      Invoke-PerformanceProbe -Command "completion" -OutputName "process-latencies-ceiling.csv" -BlueprintKey $nftBlueprintKey -Count $completionCount -Concurrency $completionConcurrency
      if (-not $CompletionOnly) {
        Invoke-PerformanceProbe -Command "realtime" -OutputName "realtime-latencies-ceiling.csv" -BlueprintKey $nftBlueprintKey -Count 1 -TimeoutSeconds 20
        Invoke-K6Script -ScriptName "admission.js" -SummaryName "k6-summary-admission-ceiling.json" -BlueprintKey $nftBlueprintKey
        Invoke-K6Script -ScriptName "search.js" -SummaryName "k6-summary-search-ceiling.json" -BlueprintKey $nftBlueprintKey
      }
    }
    elseif ($Profile -eq "Staircase") {
      Invoke-K6Script -ScriptName "staircase.js" -SummaryName "k6-summary-staircase-ceiling.json" -BlueprintKey $nftBlueprintKey
    }
    elseif ($Profile -eq "Capacity") {
      Invoke-CapacityDiscovery
    }
    else {
      Invoke-PerformanceProbe -Command "completion" -OutputName "process-latencies-ceiling.csv" -BlueprintKey $nftBlueprintKey -Count $completionCount -Concurrency $completionConcurrency
      if (-not $CompletionOnly) {
        Invoke-PerformanceProbe -Command "realtime" -OutputName "realtime-latencies-ceiling.csv" -BlueprintKey $nftBlueprintKey -Count 10 -TimeoutSeconds 20
        Invoke-K6Script -ScriptName "admission.js" -SummaryName "k6-summary-admission-ceiling.json" -BlueprintKey $nftBlueprintKey
        Invoke-K6Script -ScriptName "completion.js" -SummaryName "k6-summary-completion-ceiling.json" -BlueprintKey $nftBlueprintKey
        Invoke-K6Script -ScriptName "search.js" -SummaryName "k6-summary-search-ceiling.json" -BlueprintKey $nftBlueprintKey
      }
    }
  }

  if ($Lane -eq "product-lane" -or $Lane -eq "both") {
    $productPayload = Join-Path $repoRoot "PerformanceTests\payloads\showcase-town-weekend.json"
    Invoke-PerformanceProbe -Command "completion" -OutputName "process-latencies-product.csv" -BlueprintKey $productBlueprintKey -Count 1 -Concurrency 1 -PayloadPath $productPayload
    Invoke-K6Script -ScriptName "admission.js" -SummaryName "k6-summary-admission-product.json" -BlueprintKey $productBlueprintKey -PayloadPath $productPayload
  }

  Write-Summary
  Write-Step "NFT run completed. Artifacts: $runRoot"
}
finally {
  foreach ($process in $counterProcesses | Where-Object { $_ }) {
    try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
  }

  if ($dockerStatsProcess) {
    try { Stop-Process -Id $dockerStatsProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
  }

  if ($appHostProcess -and -not $KeepAppHost) {
    Stop-ProcessTree -ProcessId $appHostProcess.Id
    Stop-StaleAspireAppHost
  }

  if ($KeepAppHost) {
    Write-Step "Keeping AppHost running with PID $($appHostProcess.Id)."
  }
}

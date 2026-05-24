param(
  [string] $ApiBaseUrl = "http://localhost:5180",
  [string] $OutputRoot = "",
  [switch] $NoBuild,
  [string[]] $ProbeKeys = @(),
  [string[]] $CapacityCandidates = @("128", "224", "448"),
  [int] $LatencyTimeoutSeconds = 120,
  [int] $CapacityTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$probeProjectPath = Join-Path $repoRoot "PerformanceTests\WorkflowBuilder.PerformanceProbe\WorkflowBuilder.PerformanceProbe.csproj"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
  $OutputRoot = Join-Path $repoRoot ".artifacts\nft-feedback\$timestamp"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$capacityCandidateValues = @(
  $CapacityCandidates |
    ForEach-Object { $_ -split "," } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object {
      $value = 0
      if (-not [int]::TryParse($_.Trim(), [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref] $value)) {
        throw "Invalid capacity candidate '$($_)'. Use plain integers such as 128,224,448."
      }

      $value
    }
)

$requestedProbeKeys = @(
  $ProbeKeys |
    ForEach-Object { $_ -split "," } |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ } |
    ForEach-Object { $_.ToLowerInvariant() }
)

function Test-ProbeEnabled([string] $Name) {
  if ($requestedProbeKeys.Count -eq 0) {
    return $true
  }

  $normalized = $Name.ToLowerInvariant()
  return $requestedProbeKeys -contains $normalized -or
    ($normalized.StartsWith("capacity-", [StringComparison]::Ordinal) -and $requestedProbeKeys -contains "capacity") -or
    ($normalized.StartsWith("latency-", [StringComparison]::Ordinal) -and $requestedProbeKeys -contains "latency")
}

function Invoke-FeedbackProbe {
  param(
    [string] $Name,
    [string] $BlueprintKeys,
    [int] $Count,
    [int] $Concurrency,
    [int] $TimeoutSeconds,
    [string[]] $ExtraArguments = @()
  )

  $outputPath = Join-Path $OutputRoot "$Name.csv"
  $arguments = @(
    "run",
    "--project",
    $probeProjectPath,
    "-c",
    "Release"
  )
  if ($NoBuild) {
    $arguments += "--no-build"
  }

  $arguments += @(
    "--",
    "completion",
    "--api-base-url",
    $ApiBaseUrl,
    "--output",
    $outputPath,
    "--count",
    $Count.ToString(),
    "--concurrency",
    $Concurrency.ToString(),
    "--timeout-seconds",
    $TimeoutSeconds.ToString()
  )

  if ($BlueprintKeys.Contains(",")) {
    $arguments += @("--blueprint-keys", $BlueprintKeys)
  }
  else {
    $arguments += @("--blueprint-key", $BlueprintKeys)
  }

  if ($ExtraArguments.Count -gt 0) {
    $arguments += $ExtraArguments
  }

  Write-Host "[feedback-nft] $Name count=$Count concurrency=$Concurrency blueprint=$BlueprintKeys"
  $probeOutput = & dotnet @arguments 2>&1
  $probeOutput | Set-Content -LiteralPath (Join-Path $OutputRoot "$Name.log") -Encoding UTF8
  if ($LASTEXITCODE -ne 0) {
    throw "Probe '$Name' failed with exit code $LASTEXITCODE. See $Name.log."
  }

  $rows = @(Import-Csv -LiteralPath $outputPath)
  $completedRows = @($rows | Where-Object { $_.status -eq "Completed" })
  $durations = @($completedRows |
    Where-Object { $_.latencyMs -and $_.latencyMs -ne "timeout" } |
    ForEach-Object { [double] $_.latencyMs } |
    Sort-Object)
  $queueWait = @($rows |
    Where-Object { $_.queueToFirstStepStartMs -and $_.queueToFirstStepStartMs -ne "timeout" } |
    ForEach-Object { [double] $_.queueToFirstStepStartMs } |
    Sort-Object)
  $stepDurations = @($rows |
    Where-Object { $_.stepDurationMs -and $_.stepDurationMs -ne "timeout" } |
    ForEach-Object { [double] $_.stepDurationMs } |
    Sort-Object)
  $postLatency = @($rows |
    Where-Object { $_.postLatencyMs -and $_.postLatencyMs -ne "timeout" } |
    ForEach-Object { [double] $_.postLatencyMs } |
    Sort-Object)

  $completed = $completedRows.Count
  $failed = [Math]::Max(0, $rows.Count - $completed)
  $timedOut = @($rows | Where-Object { $_.status -eq "Timeout" }).Count
  $firstStart = @($rows | Where-Object acceptedUtc | Sort-Object acceptedUtc | Select-Object -First 1)[0]
  $lastTerminal = @($completedRows | Where-Object completedUtc | Sort-Object completedUtc -Descending | Select-Object -First 1)[0]
  $elapsedSeconds = $null
  if ($firstStart -and $lastTerminal) {
    $elapsedSeconds = ([DateTimeOffset]::Parse($lastTerminal.completedUtc) - [DateTimeOffset]::Parse($firstStart.acceptedUtc)).TotalSeconds
  }

  [pscustomobject]@{
    name = $Name
    blueprintKeys = $BlueprintKeys
    count = $Count
    concurrency = $Concurrency
    timeoutSeconds = $TimeoutSeconds
    completed = $completed
    failed = $failed
    timedOut = $timedOut
    passed = $completed -eq $Count -and (($durations | Select-Object -Last 1) -le ($TimeoutSeconds * 1000))
    completedPerSecond = if ($elapsedSeconds -and $elapsedSeconds -gt 0) { $completed / $elapsedSeconds } else { $null }
    processDurationP50Ms = Get-Percentile $durations 50
    processDurationP95Ms = Get-Percentile $durations 95
    processDurationP99Ms = Get-Percentile $durations 99
    postLatencyP95Ms = Get-Percentile $postLatency 95
    queueToFirstStepStartP95Ms = Get-Percentile $queueWait 95
    stepDurationP95Ms = Get-Percentile $stepDurations 95
    csv = $outputPath
    log = Join-Path $OutputRoot "$Name.log"
  }
}

function Get-Percentile {
  param(
    [double[]] $Values,
    [double] $Percentile
  )

  if ($Values.Count -eq 0) {
    return $null
  }

  $rank = ($Percentile / 100.0) * ($Values.Count - 1)
  $lower = [Math]::Floor($rank)
  $upper = [Math]::Ceiling($rank)
  if ($lower -eq $upper) {
    return [Math]::Round($Values[$lower], 3)
  }

  $weight = $rank - $lower
  return [Math]::Round(($Values[$lower] * (1 - $weight)) + ($Values[$upper] * $weight), 3)
}

function Format-Milliseconds([object] $Value) {
  if ($null -eq $Value) {
    return "n/a"
  }

  if ([double] $Value -ge 1000) {
    return "$([Math]::Round(([double] $Value) / 1000, 2))s"
  }

  return "$([Math]::Round([double] $Value, 1))ms"
}

try {
  $health = Invoke-WebRequest -Uri "$ApiBaseUrl/health" -UseBasicParsing -TimeoutSec 10
  if ($health.StatusCode -ne 200) {
    throw "Health endpoint returned $($health.StatusCode)."
  }
}
catch {
  throw "API is not healthy at $ApiBaseUrl. Start the NFT AppHost first. $($_.Exception.Message)"
}

if (-not $NoBuild) {
  dotnet build $probeProjectPath -c Release | Out-Host
}

$laneBlueprints = "nft-linear-lane-1-single-step,nft-linear-lane-2-single-step,nft-linear-lane-3-single-step,nft-linear-lane-4-single-step,nft-linear-lane-5-single-step"
$results = [System.Collections.Generic.List[object]]::new()
if (Test-ProbeEnabled "latency-one-node") {
  $results.Add((Invoke-FeedbackProbe -Name "latency-one-node" -BlueprintKeys "nft-linear-single-step" -Count 10 -Concurrency 1 -TimeoutSeconds $LatencyTimeoutSeconds))
}
if (Test-ProbeEnabled "latency-plugin-light") {
  $results.Add((Invoke-FeedbackProbe -Name "latency-plugin-light" -BlueprintKeys "nft-linear-alpha-single-step" -Count 10 -Concurrency 1 -TimeoutSeconds $LatencyTimeoutSeconds))
}
if (Test-ProbeEnabled "full-linear-10") {
  $results.Add((Invoke-FeedbackProbe -Name "full-linear-10" -BlueprintKeys "nft-score-linear-10" -Count 10 -Concurrency 2 -TimeoutSeconds $LatencyTimeoutSeconds))
}
if (Test-ProbeEnabled "full-fanout-10") {
  $results.Add((Invoke-FeedbackProbe -Name "full-fanout-10" -BlueprintKeys "nft-score-fanout-10" -Count 10 -Concurrency 2 -TimeoutSeconds $LatencyTimeoutSeconds))
}

foreach ($candidate in $capacityCandidateValues) {
  $name = "capacity-$candidate"
  if (Test-ProbeEnabled $name) {
    $results.Add((Invoke-FeedbackProbe -Name $name -BlueprintKeys $laneBlueprints -Count $candidate -Concurrency $candidate -TimeoutSeconds $CapacityTimeoutSeconds))
  }
}

if ($results.Count -eq 0) {
  throw "No feedback probes were selected. Use keys like latency, latency-one-node, full-linear-10, full-fanout-10, capacity, or capacity-224."
}

$summary = [ordered]@{
  generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
  apiBaseUrl = $ApiBaseUrl
  outputRoot = (Resolve-Path $OutputRoot).Path
  results = $results.ToArray()
}
$summaryPath = Join-Path $OutputRoot "feedback-summary.json"
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

$md = [System.Collections.Generic.List[string]]::new()
$md.Add("# WorkflowBuilder NFT Feedback")
$md.Add("")
$md.Add("- Generated: ``$($summary.generatedAtUtc)``")
$md.Add("- API: ``$ApiBaseUrl``")
$md.Add("")
$md.Add("| Probe | Completed | Timed out | Throughput | P50 | P95 | P99 | POST P95 | Queue P95 | Step P95 | Passed |")
$md.Add("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|")
foreach ($result in $results) {
  $throughput = if ($result.completedPerSecond) { [Math]::Round($result.completedPerSecond, 2).ToString() } else { "n/a" }
  $md.Add("| $($result.name) | $($result.completed)/$($result.count) | $($result.timedOut) | $throughput | $(Format-Milliseconds $result.processDurationP50Ms) | $(Format-Milliseconds $result.processDurationP95Ms) | $(Format-Milliseconds $result.processDurationP99Ms) | $(Format-Milliseconds $result.postLatencyP95Ms) | $(Format-Milliseconds $result.queueToFirstStepStartP95Ms) | $(Format-Milliseconds $result.stepDurationP95Ms) | $($result.passed) |")
}

$mdPath = Join-Path $OutputRoot "feedback-summary.md"
$md | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host "[feedback-nft] wrote $summaryPath"
Write-Host "[feedback-nft] wrote $mdPath"

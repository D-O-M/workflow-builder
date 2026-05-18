param(
  [string] $RunRoot = "",
  [switch] $Latest
)

$ErrorActionPreference = "Stop"

function Resolve-RunRoot {
  param([string] $RequestedRunRoot)

  if (-not [string]::IsNullOrWhiteSpace($RequestedRunRoot)) {
    return [System.IO.Path]::GetFullPath($RequestedRunRoot)
  }

  $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
  $latestRun = Get-ChildItem -LiteralPath (Join-Path $repoRoot ".artifacts\nft") -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "capacity-summary.csv") } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

  if (-not $latestRun) {
    throw "No NFT run with capacity-summary.csv was found."
  }

  return $latestRun.FullName
}

function Get-PercentileValue {
  param(
    [double[]] $Values,
    [double] $Percentile
  )

  if ($Values.Count -eq 0) {
    return $null
  }

  $ordered = @($Values | Sort-Object)
  $index = [Math]::Ceiling($Percentile * $ordered.Count) - 1
  $index = [Math]::Clamp($index, 0, $ordered.Count - 1)
  return [double] $ordered[$index]
}

function Format-Ms {
  param([object] $Value)

  if ($null -eq $Value -or $Value -is [System.DBNull]) {
    return "n/a"
  }

  $number = [double] $Value
  if ([double]::IsNaN($number)) {
    return "n/a"
  }

  if ($number -ge 1000) {
    return ("{0:n2}s" -f ($number / 1000))
  }

  return ("{0:n1}ms" -f $number)
}

function Format-PhaseValue {
  param(
    [object] $Value,
    [string] $Phase
  )

  if ($Phase -eq "pollCount") {
    if ($null -eq $Value -or $Value -is [System.DBNull]) {
      return "n/a"
    }

    $number = [double] $Value
    if ([double]::IsNaN($number)) {
      return "n/a"
    }

    return ("{0:n0}" -f $number)
  }

  return Format-Ms $Value
}

function Format-Bytes {
  param([object] $Value)

  if ($null -eq $Value -or $Value -is [System.DBNull]) {
    return "n/a"
  }

  $number = [double] $Value
  if ([double]::IsNaN($number)) {
    return "n/a"
  }

  $units = @("B", "KiB", "MiB", "GiB", "TiB")
  $unitIndex = 0
  while ([Math]::Abs($number) -ge 1024 -and $unitIndex -lt $units.Count - 1) {
    $number /= 1024
    $unitIndex++
  }

  return ("{0:n2} {1}" -f $number, $units[$unitIndex])
}

function Format-CounterValue {
  param(
    [object] $Value,
    [string] $Metric,
    [string] $Unit
  )

  if ($null -eq $Value -or $Value -is [System.DBNull]) {
    return "n/a"
  }

  $number = [double] $Value
  if ([double]::IsNaN($number)) {
    return "n/a"
  }

  if ($Unit -eq "By") {
    return Format-Bytes $number
  }

  if ($Unit -eq "s" -or $Unit.StartsWith("s ", [StringComparison]::Ordinal)) {
    return Format-Ms ($number * 1000)
  }

  if ($Unit -eq "ms" -or $Metric.EndsWith("_ms", [StringComparison]::OrdinalIgnoreCase)) {
    return Format-Ms $number
  }

  return ("{0:n2}" -f $number)
}

function Get-NumericColumn {
  param(
    [object[]] $Rows,
    [string] $Column
  )

  if ($Rows.Count -eq 0 -or -not ($Rows[0].PSObject.Properties.Name -contains $Column)) {
    return @()
  }

  return @($Rows |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_.$Column) } |
    ForEach-Object { [double] $_.$Column })
}

function Get-CsvTimingSummary {
  param([string] $CsvPath)

  $rows = @(Import-Csv -LiteralPath $CsvPath)
  $latencies = Get-NumericColumn -Rows $rows -Column "latencyMs"
  $completed = @($rows | Where-Object { $_.status -eq "Completed" })
  $columns = if ($rows.Count -gt 0) { $rows[0].PSObject.Properties.Name } else { @() }
  $phaseColumns = @(
    "postLatencyMs",
    "acceptedToPostReturnMs",
    "queueToFirstStepStartMs",
    "stepDurationMs",
    "terminalObservationLagMs",
    "pollCount"
  )

  $phaseSummaries = foreach ($column in $phaseColumns) {
    $values = Get-NumericColumn -Rows $rows -Column $column
    if ($values.Count -eq 0) {
      continue
    }

    [pscustomobject]@{
      phase = $column
      p50 = Get-PercentileValue -Values $values -Percentile 0.50
      p95 = Get-PercentileValue -Values $values -Percentile 0.95
      p99 = Get-PercentileValue -Values $values -Percentile 0.99
      max = ($values | Measure-Object -Maximum).Maximum
    }
  }

  $byBlueprint = @($rows |
    Group-Object blueprintKey |
    ForEach-Object {
      $groupRows = @($_.Group)
      $groupLatencies = Get-NumericColumn -Rows $groupRows -Column "latencyMs"
      [pscustomobject]@{
        blueprintKey = $_.Name
        count = $groupRows.Count
        p50Ms = Get-PercentileValue -Values $groupLatencies -Percentile 0.50
        p95Ms = Get-PercentileValue -Values $groupLatencies -Percentile 0.95
        maxMs = if ($groupLatencies.Count -gt 0) { ($groupLatencies | Measure-Object -Maximum).Maximum } else { $null }
      }
    })

  [pscustomobject]@{
    csv = $CsvPath
    rowCount = $rows.Count
    completedCount = $completed.Count
    p50Ms = Get-PercentileValue -Values $latencies -Percentile 0.50
    p95Ms = Get-PercentileValue -Values $latencies -Percentile 0.95
    p99Ms = Get-PercentileValue -Values $latencies -Percentile 0.99
    maxMs = if ($latencies.Count -gt 0) { ($latencies | Measure-Object -Maximum).Maximum } else { $null }
    hasDetailedColumns = @($phaseSummaries).Count -gt 0
    phases = @($phaseSummaries)
    byBlueprint = $byBlueprint
    columns = $columns
  }
}

function Get-CounterMetricName {
  param([string] $CounterName)

  $prefix = $CounterName
  $paren = $prefix.IndexOf(" (", [StringComparison]::Ordinal)
  if ($paren -ge 0) {
    $prefix = $prefix.Substring(0, $paren)
  }

  $bracket = $prefix.IndexOf("[", [StringComparison]::Ordinal)
  if ($bracket -ge 0) {
    $prefix = $prefix.Substring(0, $bracket)
  }

  return $prefix.Trim()
}

function Get-CounterPercentile {
  param([string] $CounterName)

  if ($CounterName -match "Percentile=(\d+)") {
    return $Matches[1]
  }

  return $null
}

function Get-CounterUnit {
  param([string] $CounterName)

  if ($CounterName -match "^[^(]+\(([^)]+)\)") {
    return $Matches[1].Trim()
  }

  return ""
}

function Get-CounterTagValue {
  param(
    [string] $CounterName,
    [string] $TagName
  )

  $open = $CounterName.IndexOf("[", [StringComparison]::Ordinal)
  $close = $CounterName.LastIndexOf("]", [StringComparison]::Ordinal)
  if ($open -lt 0 -or $close -le $open) {
    return $null
  }

  $tagText = $CounterName.Substring($open + 1, $close - $open - 1)
  foreach ($pair in $tagText.Split(";", [StringSplitOptions]::RemoveEmptyEntries)) {
    $parts = $pair.Split("=", 2)
    if ($parts.Count -eq 2 -and $parts[0] -eq $TagName) {
      return $parts[1]
    }
  }

  return $null
}

function Test-CounterTagFilters {
  param(
    [object] $Row,
    [hashtable] $TagFilters
  )

  foreach ($key in $TagFilters.Keys) {
    $actual = Get-CounterTagValue -CounterName $Row.'Counter Name' -TagName $key
    if ($actual -ne $TagFilters[$key]) {
      return $false
    }
  }

  return $true
}

function Get-CounterSummary {
  param(
    [string] $CsvPath,
    [string[]] $Metrics,
    [string] $Label = "",
    [hashtable] $TagFilters = @{}
  )

  if (-not (Test-Path -LiteralPath $CsvPath)) {
    return @()
  }

  $rows = @(Import-Csv -LiteralPath $CsvPath)
  foreach ($metric in $Metrics) {
    $matching = @($rows | Where-Object {
        (Get-CounterMetricName $_.'Counter Name') -eq $metric -and
        (Test-CounterTagFilters -Row $_ -TagFilters $TagFilters)
      })
    if ($matching.Count -eq 0) {
      continue
    }

    $unit = Get-CounterUnit $matching[0].'Counter Name'

    $percentileGroups = @($matching |
      Group-Object { Get-CounterPercentile $_.'Counter Name' } |
      Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) })

    if ($percentileGroups.Count -gt 0) {
      foreach ($group in $percentileGroups) {
        $values = @($group.Group | ForEach-Object { [double] $_.'Mean/Increment' })
        [pscustomobject]@{
          metric = $metric
          label = if ([string]::IsNullOrWhiteSpace($Label)) { $metric } else { $Label }
          unit = $unit
          percentile = "p$($group.Name)"
          max = ($values | Measure-Object -Maximum).Maximum
          median = Get-PercentileValue -Values $values -Percentile 0.50
          samples = $values.Count
        }
      }
      continue
    }

    $values = @($matching | ForEach-Object { [double] $_.'Mean/Increment' })
    [pscustomobject]@{
      metric = $metric
      label = if ([string]::IsNullOrWhiteSpace($Label)) { $metric } else { $Label }
      unit = $unit
      percentile = ""
      max = ($values | Measure-Object -Maximum).Maximum
      median = Get-PercentileValue -Values $values -Percentile 0.50
      samples = $values.Count
    }
  }
}

function Get-RateSummary {
  param(
    [string] $CsvPath,
    [string[]] $Metrics
  )

  if (-not (Test-Path -LiteralPath $CsvPath)) {
    return @()
  }

  $rows = @(Import-Csv -LiteralPath $CsvPath)
  foreach ($metric in $Metrics) {
    $matching = @($rows | Where-Object { (Get-CounterMetricName $_.'Counter Name') -eq $metric })
    if ($matching.Count -eq 0) {
      continue
    }

    $byTimestamp = @($matching |
      Group-Object Timestamp |
      ForEach-Object {
        [pscustomobject]@{
          timestamp = $_.Name
          total = ($_.Group | ForEach-Object { [double] $_.'Mean/Increment' } | Measure-Object -Sum).Sum
        }
      })

    $unit = if ($matching.Count -gt 0) { Get-CounterUnit $matching[0].'Counter Name' } else { "" }
    [pscustomobject]@{
      metric = $metric
      unit = $unit
      maxPerInterval = ($byTimestamp.total | Measure-Object -Maximum).Maximum
      medianPerInterval = Get-PercentileValue -Values @($byTimestamp.total) -Percentile 0.50
      samples = $byTimestamp.Count
    }
  }
}

function Write-TimingReport {
  param(
    [string] $ResolvedRunRoot,
    [object] $CapacitySummary,
    [object[]] $CandidateSummaries,
    [object[]] $ApiCounterSummary,
    [object[]] $EngineCounterSummary,
    [object[]] $EngineRateSummary
  )

  $lines = [System.Collections.Generic.List[string]]::new()
  $lines.Add("# NFT Timing Analysis")
  $lines.Add("")
  $lines.Add("- Run: ``$ResolvedRunRoot``")
  $lines.Add("- Strict timeout: $($CapacitySummary.timeoutSeconds)s")
  $lines.Add("- Max passing processes: $($CapacitySummary.maxPassingProcesses)")
  $lines.Add("- Max passing tasks: $($CapacitySummary.maxPassingTasks)")
  $lines.Add("")

  $lines.Add("## Candidate Latency")
  $lines.Add("")
  $lines.Add("| Concurrency | Passed | Completed | P50 | P95 | P99 | Max | Detailed phases |")
  $lines.Add("|---:|:---:|---:|---:|---:|---:|---:|:---:|")
  foreach ($candidate in $CapacitySummary.candidates | Sort-Object concurrency) {
    $summary = $CandidateSummaries | Where-Object { [System.IO.Path]::GetFullPath($_.csv) -eq [System.IO.Path]::GetFullPath($candidate.csv) } | Select-Object -First 1
    $lines.Add((
        "| {0} | {1} | {2}/{3} | {4} | {5} | {6} | {7} | {8} |" -f
        $candidate.concurrency,
        $candidate.passed,
        $candidate.completedProcesses,
        $candidate.totalProcesses,
        (Format-Ms $summary.p50Ms),
        (Format-Ms $summary.p95Ms),
        (Format-Ms $summary.p99Ms),
        (Format-Ms $summary.maxMs),
        $(if ($summary.hasDetailedColumns) { "yes" } else { "no" })))
  }
  $lines.Add("")

  $bestCandidate = $CapacitySummary.candidates |
    Where-Object passed |
    Sort-Object concurrency -Descending |
    Select-Object -First 1
  $bestSummary = $CandidateSummaries |
    Where-Object { [System.IO.Path]::GetFullPath($_.csv) -eq [System.IO.Path]::GetFullPath($bestCandidate.csv) } |
    Select-Object -First 1

  if ($bestSummary) {
    $lines.Add("## Best Passing Candidate")
    $lines.Add("")
    $lines.Add("- Candidate: $($bestCandidate.concurrency) concurrent process(es)")
    $lines.Add("- Overall P95: $(Format-Ms $bestSummary.p95Ms)")
    $lines.Add("- Overall max: $(Format-Ms $bestSummary.maxMs)")
    $lines.Add("")

    if ($bestSummary.byBlueprint.Count -gt 0) {
      $lines.Add("### Per Blueprint")
      $lines.Add("")
      $lines.Add("| Blueprint | Count | P50 | P95 | Max |")
      $lines.Add("|---|---:|---:|---:|---:|")
      foreach ($blueprint in $bestSummary.byBlueprint) {
        $lines.Add("| $($blueprint.blueprintKey) | $($blueprint.count) | $(Format-Ms $blueprint.p50Ms) | $(Format-Ms $blueprint.p95Ms) | $(Format-Ms $blueprint.maxMs) |")
      }
      $lines.Add("")
    }

    if ($bestSummary.phases.Count -gt 0) {
      $lines.Add("### Client/Process Phases")
      $lines.Add("")
      $lines.Add("| Phase | P50 | P95 | P99 | Max |")
      $lines.Add("|---|---:|---:|---:|---:|")
      foreach ($phase in $bestSummary.phases) {
        $lines.Add("| $($phase.phase) | $(Format-PhaseValue -Value $phase.p50 -Phase $phase.phase) | $(Format-PhaseValue -Value $phase.p95 -Phase $phase.phase) | $(Format-PhaseValue -Value $phase.p99 -Phase $phase.phase) | $(Format-PhaseValue -Value $phase.max -Phase $phase.phase) |")
      }
      $lines.Add("")
    }
    else {
      $lines.Add("Detailed client/process phase columns were not present in this run. Re-run with the updated probe to populate POST, queue-to-start, step-duration, terminal-observation, and poll-count columns.")
      $lines.Add("")
    }
  }

  $lines.Add("## Runtime Counters")
  $lines.Add("")
  if ($ApiCounterSummary.Count -gt 0) {
    $lines.Add("### API")
    $lines.Add("")
    $lines.Add("| Metric | Percentile | Median sample | Max sample | Samples |")
    $lines.Add("|---|---:|---:|---:|---:|")
    foreach ($counter in $ApiCounterSummary) {
      $lines.Add("| $($counter.label) | $($counter.percentile) | $(Format-CounterValue -Value $counter.median -Metric $counter.metric -Unit $counter.unit) | $(Format-CounterValue -Value $counter.max -Metric $counter.metric -Unit $counter.unit) | $($counter.samples) |")
    }
    $lines.Add("")
  }

  if ($EngineCounterSummary.Count -gt 0) {
    $lines.Add("### Engine")
    $lines.Add("")
    $lines.Add("| Metric | Percentile | Median sample | Max sample | Samples |")
    $lines.Add("|---|---:|---:|---:|---:|")
    foreach ($counter in $EngineCounterSummary) {
      $lines.Add("| $($counter.label) | $($counter.percentile) | $(Format-CounterValue -Value $counter.median -Metric $counter.metric -Unit $counter.unit) | $(Format-CounterValue -Value $counter.max -Metric $counter.metric -Unit $counter.unit) | $($counter.samples) |")
    }
    $lines.Add("")
  }

  if ($EngineRateSummary.Count -gt 0) {
    $lines.Add("### Engine Rates")
    $lines.Add("")
    $lines.Add("| Metric | Median per 3s | Max per 3s | Samples |")
    $lines.Add("|---|---:|---:|---:|")
    foreach ($rate in $EngineRateSummary) {
      $lines.Add("| $($rate.metric) | $(Format-CounterValue -Value $rate.medianPerInterval -Metric $rate.metric -Unit $rate.unit) | $(Format-CounterValue -Value $rate.maxPerInterval -Metric $rate.metric -Unit $rate.unit) | $($rate.samples) |")
    }
    $lines.Add("")
  }

  $lines.Add("## Reading Notes")
  $lines.Add("")
  $lines.Add("- Candidate CSV latency is client-observed wall-clock latency, not pure Engine execution time.")
  $lines.Add("- Client/process phases split that wall-clock into POST admission, API return delay, queue-to-first-step-start, recorded step duration, terminal observation lag, and poll count.")
  $lines.Add("- API POST duration comes from ASP.NET counters and includes API-side blueprint resolution, Cosmos writes, queue enqueue, and HTTP response work.")
  $lines.Add("- Engine plugin start delay is the backlog between Engine enqueueing work to the plugin process and the harness actually beginning that invocation.")
  $lines.Add("- Engine plugin hydration and execute timings are reported by the plugin harness and sent back in the plugin execution result.")
  $lines.Add("- Cosmos duration and request-unit counters are process-local: API counters describe API Cosmos calls, Engine counters describe Engine Cosmos calls.")

  $reportPath = Join-Path $ResolvedRunRoot "timing-analysis.md"
  $jsonPath = Join-Path $ResolvedRunRoot "timing-analysis.json"
  $lines | Set-Content -LiteralPath $reportPath -Encoding UTF8

  [ordered]@{
    runRoot = $ResolvedRunRoot
    capacity = $CapacitySummary
    candidates = $CandidateSummaries
    apiCounters = $ApiCounterSummary
    engineCounters = $EngineCounterSummary
    engineRates = $EngineRateSummary
  } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

  Write-Host "Wrote $reportPath"
  Write-Host "Wrote $jsonPath"
}

$resolvedRunRoot = Resolve-RunRoot $RunRoot
$capacitySummaryPath = Join-Path $resolvedRunRoot "capacity-summary.json"
if (-not (Test-Path -LiteralPath $capacitySummaryPath)) {
  throw "No capacity-summary.json found under '$resolvedRunRoot'."
}

$capacitySummary = Get-Content -LiteralPath $capacitySummaryPath -Raw | ConvertFrom-Json
$candidateSummaries = @($capacitySummary.candidates | ForEach-Object { Get-CsvTimingSummary -CsvPath $_.csv })

$apiCounterSummary = @(
  Get-CounterSummary `
    -CsvPath (Join-Path $resolvedRunRoot "dotnet-counters-api.csv") `
    -Metrics @("http.server.request.duration") `
    -Label "POST start process" `
    -TagFilters @{
      "http.request.method" = "POST"
      "http.route" = "api/blueprints/{Id:int}/processes"
    }
  Get-CounterSummary `
    -CsvPath (Join-Path $resolvedRunRoot "dotnet-counters-api.csv") `
    -Metrics @("http.server.request.duration") `
    -Label "GET process detail polling" `
    -TagFilters @{
      "http.request.method" = "GET"
      "http.route" = "api/blueprints/{blueprintDefinitionId:int}/processes/{processId:guid}"
    }
  Get-CounterSummary `
    -CsvPath (Join-Path $resolvedRunRoot "dotnet-counters-api.csv") `
    -Metrics @(
      "process_start_resolve_definition_ms",
      "process_start_load_graph_ms",
      "process_start_validate_overrides_ms",
      "process_start_materialize_initial_input_ms",
      "process_start_create_process_state_ms",
      "process_start_create_initial_step_ms",
      "process_start_enqueue_initial_step_ms",
      "process_start_total_ms",
      "cosmos_operation_duration_ms",
      "cosmos_request_units",
      "cosmos_request_units_total",
      "dotnet.process.memory.working_set",
      "dotnet.gc.heap.total_allocated"
    )
)
$engineCounterSummary = @(
  Get-CounterSummary `
    -CsvPath (Join-Path $resolvedRunRoot "dotnet-counters-engine.csv") `
    -Metrics @(
      "process_step_duration_ms",
      "process_step_queue_wait_ms",
      "process_step_claim_ms",
      "process_step_agent_enqueue_ms",
      "process_step_plugin_start_delay_ms",
      "process_step_finalize_ms",
      "process_step_recorded_duration_ms",
      "plugin_hydration_ms",
      "plugin_execute_ms",
      "cosmos_operation_duration_ms",
      "cosmos_request_units",
      "cosmos_request_units_total",
      "dotnet.process.memory.working_set",
      "dotnet.gc.heap.total_allocated"
    )
)
$engineRateSummary = @(
  Get-RateSummary `
    -CsvPath (Join-Path $resolvedRunRoot "dotnet-counters-engine.csv") `
    -Metrics @("process_step_started_total", "process_step_completed_total", "process_step_failed_total")
)

Write-TimingReport `
  -ResolvedRunRoot $resolvedRunRoot `
  -CapacitySummary $capacitySummary `
  -CandidateSummaries $candidateSummaries `
  -ApiCounterSummary $apiCounterSummary `
  -EngineCounterSummary $engineCounterSummary `
  -EngineRateSummary $engineRateSummary

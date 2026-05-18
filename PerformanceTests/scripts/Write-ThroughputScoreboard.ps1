[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)] [string] $RunRoot,
  [Parameter(Mandatory = $true)] [string] $ManifestPath,
  [ValidateSet("Smoke", "Standard", "Staircase", "Capacity", "Scoreboard")]
  [string] $Profile = "Scoreboard",
  [ValidateSet("Off", "Warn", "Fail")]
  [string] $GateMode = "Warn",
  [string] $BaselinePath = "",
  [string] $PrometheusBaseUrl = ""
)

$ErrorActionPreference = "Stop"

$resolvedRunRoot = [System.IO.Path]::GetFullPath($RunRoot)
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$windowsPath = Join-Path $resolvedRunRoot "scenario-windows.json"
$runConfigPath = Join-Path $resolvedRunRoot "run-config.json"
$runConfig = if (Test-Path -LiteralPath $runConfigPath) {
  Get-Content -LiteralPath $runConfigPath -Raw | ConvertFrom-Json
}
else {
  $null
}
$windows = if (Test-Path -LiteralPath $windowsPath) {
  @(Get-Content -LiteralPath $windowsPath -Raw | ConvertFrom-Json)
}
else {
  @()
}
$windowsByScenario = @{}
foreach ($window in $windows) {
  $windowsByScenario[[string]$window.key] = $window
}

$baseline = $null
if (-not [string]::IsNullOrWhiteSpace($BaselinePath) -and (Test-Path -LiteralPath $BaselinePath)) {
  $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
}

$counterCache = @{}

function Get-PropertyValue($Object, [string] $Name, $Fallback = $null) {
  if ($null -eq $Object) {
    return $Fallback
  }

  if ($Object.PSObject.Properties.Name -contains $Name) {
    return $Object.$Name
  }

  return $Fallback
}

function Get-ScenarioProfile($Scenario, [string] $ProfileName) {
  $profiles = Get-PropertyValue $Scenario "profiles" $null
  if ($null -eq $profiles) {
    return $null
  }

  return Get-PropertyValue $profiles $ProfileName $null
}

function Get-ProfileDefaults([string] $ProfileName) {
  return Get-PropertyValue $manifest.profiles $ProfileName $null
}

function Get-ScenarioSetting($Scenario, [string] $ProfileName, [string] $Name, $Fallback = $null) {
  $scenarioProfile = Get-ScenarioProfile $Scenario $ProfileName
  $profileValue = Get-PropertyValue $scenarioProfile $Name $null
  if ($null -ne $profileValue) {
    return $profileValue
  }

  $profileDefaults = Get-ProfileDefaults $ProfileName
  $defaultValue = Get-PropertyValue $profileDefaults $Name $null
  if ($null -ne $defaultValue) {
    return $defaultValue
  }

  return $Fallback
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
  $index = [Math]::Max(0, [Math]::Min($ordered.Count - 1, $index))
  return [double] $ordered[$index]
}

function Convert-ToUtcDateTimeOffset($Value) {
  if ($null -eq $Value) {
    return $null
  }

  if ($Value -is [DateTimeOffset]) {
    return $Value.ToUniversalTime()
  }

  if ($Value -is [DateTime]) {
    return [DateTimeOffset]::new([DateTime]::SpecifyKind($Value, [DateTimeKind]::Utc)).ToUniversalTime()
  }

  return [DateTimeOffset]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
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

function Get-DateColumn {
  param(
    [object[]] $Rows,
    [string] $Column
  )

  if ($Rows.Count -eq 0 -or -not ($Rows[0].PSObject.Properties.Name -contains $Column)) {
    return @()
  }

  return @($Rows |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_.$Column) } |
    ForEach-Object { Convert-ToUtcDateTimeOffset $_.$Column })
}

function Get-DurationSeconds($Window, [object[]] $Rows) {
  if ($Window -and $Window.startUtc -and $Window.endUtc) {
    $start = Convert-ToUtcDateTimeOffset $Window.startUtc
    $end = Convert-ToUtcDateTimeOffset $Window.endUtc
    $seconds = ($end - $start).TotalSeconds
    if ($seconds -gt 0) {
      return $seconds
    }
  }

  $timestamps = @()
  $timestamps += Get-DateColumn -Rows $Rows -Column "startedUtc"
  $timestamps += Get-DateColumn -Rows $Rows -Column "completedUtc"
  if ($timestamps.Count -lt 2) {
    return 1
  }

  $ordered = @($timestamps | Sort-Object)
  return [Math]::Max(1, ($ordered[-1] - $ordered[0]).TotalSeconds)
}

function Get-K6MetricValue([object] $Summary, [string] $MetricName, [string] $ValueName) {
  if ($null -eq $Summary -or $null -eq $Summary.metrics) {
    return $null
  }

  $metric = Get-PropertyValue $Summary.metrics $MetricName $null
  if ($null -eq $metric) {
    return $null
  }

  $values = Get-PropertyValue $metric "values" $null
  if ($null -eq $values) {
    return $null
  }

  return Get-PropertyValue $values $ValueName $null
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

function Get-CounterValue {
  param([object] $Row)

  foreach ($column in @("Mean/Increment", "Increment", "Value", "Mean", "Rate / sec", "Count")) {
    if ($Row.PSObject.Properties.Name -contains $column -and -not [string]::IsNullOrWhiteSpace($Row.$column)) {
      return [double] $Row.$column
    }
  }

  return $null
}

function Try-ParseCounterTimestamp {
  param([object] $Row)

  if (-not ($Row.PSObject.Properties.Name -contains "Timestamp") -or [string]::IsNullOrWhiteSpace($Row.Timestamp)) {
    return $null
  }

  try {
    return [DateTimeOffset]::Parse([string]$Row.Timestamp).ToUniversalTime()
  }
  catch {
    return $null
  }
}

function Get-CounterRows {
  param(
    [string] $CsvPath,
    [object] $Window
  )

  if (-not (Test-Path -LiteralPath $CsvPath)) {
    return @()
  }

  if (-not $counterCache.ContainsKey($CsvPath)) {
    $counterCache[$CsvPath] = @(Import-Csv -LiteralPath $CsvPath)
  }

  $rows = @($counterCache[$CsvPath])
  if (-not $Window -or -not $Window.startUtc -or -not $Window.endUtc) {
    return $rows
  }

  $start = Convert-ToUtcDateTimeOffset $Window.startUtc
  $end = Convert-ToUtcDateTimeOffset $Window.endUtc
  $filtered = @($rows | Where-Object {
      $timestamp = Try-ParseCounterTimestamp $_
      $null -ne $timestamp -and $timestamp -ge $start -and $timestamp -le $end
    })

  return $(if ($filtered.Count -gt 0) { $filtered } else { $rows })
}

function Get-CounterSample {
  param(
    [object[]] $Rows,
    [string] $MetricName,
    [string] $Percentile = ""
  )

  $matching = @($Rows | Where-Object {
      (Get-CounterMetricName $_.'Counter Name') -eq $MetricName -and
      ([string]::IsNullOrWhiteSpace($Percentile) -or (Get-CounterPercentile $_.'Counter Name') -eq $Percentile)
    })
  if ($matching.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($Percentile)) {
    $matching = @($Rows | Where-Object { (Get-CounterMetricName $_.'Counter Name') -eq $MetricName })
  }

  $values = @($matching | ForEach-Object { Get-CounterValue $_ } | Where-Object { $null -ne $_ })
  if ($values.Count -eq 0) {
    return $null
  }

  return ($values | Measure-Object -Maximum).Maximum
}

function Get-CounterSum {
  param(
    [object[]] $Rows,
    [string] $MetricName
  )

  $values = @($Rows |
    Where-Object { (Get-CounterMetricName $_.'Counter Name') -eq $MetricName } |
    ForEach-Object { Get-CounterValue $_ } |
    Where-Object { $null -ne $_ })
  if ($values.Count -eq 0) {
    return $null
  }

  return ($values | Measure-Object -Sum).Sum
}

function Get-CounterMaxByCandidates {
  param(
    [object[]] $Rows,
    [string[]] $MetricNames
  )

  foreach ($metric in $MetricNames) {
    $value = Get-CounterSample -Rows $Rows -MetricName $metric
    if ($null -ne $value) {
      return $value
    }
  }

  return $null
}

function Get-CompletionMetrics {
  param(
    [string] $CsvPath,
    [object] $Window
  )

  if (-not (Test-Path -LiteralPath $CsvPath)) {
    return [ordered]@{
      available = $false
    }
  }

  $rows = @(Import-Csv -LiteralPath $CsvPath)
  $completedRows = @($rows | Where-Object { $_.status -eq "Completed" })
  $latencies = Get-NumericColumn -Rows $rows -Column "latencyMs"
  $stepDurations = Get-NumericColumn -Rows $rows -Column "stepDurationMs"
  $queueLag = Get-NumericColumn -Rows $rows -Column "queueToFirstStepStartMs"
  $durationSeconds = Get-DurationSeconds -Window $Window -Rows $rows
  $stepCount = if ($rows.Count -gt 0) {
    @($rows | ForEach-Object { if (-not [string]::IsNullOrWhiteSpace($_.stepCount)) { [int] $_.stepCount } } | Measure-Object -Sum).Sum
  }
  else {
    0
  }

  [ordered]@{
    available = $true
    totalProcesses = $rows.Count
    completedProcesses = $completedRows.Count
    failedOrTimedOutProcesses = $rows.Count - $completedRows.Count
    successRate = if ($rows.Count -gt 0) { $completedRows.Count / $rows.Count } else { $null }
    durationSeconds = $durationSeconds
    acceptedProcessesPerSecond = if ($durationSeconds -gt 0) { $rows.Count / $durationSeconds } else { $null }
    completedProcessesPerSecond = if ($durationSeconds -gt 0) { $completedRows.Count / $durationSeconds } else { $null }
    stepCount = [int] $stepCount
    stepsPerSecond = if ($durationSeconds -gt 0) { $stepCount / $durationSeconds } else { $null }
    processDurationP50Ms = Get-PercentileValue -Values $latencies -Percentile 0.50
    processDurationP95Ms = Get-PercentileValue -Values $latencies -Percentile 0.95
    processDurationP99Ms = Get-PercentileValue -Values $latencies -Percentile 0.99
    coarseStepDurationP50Ms = Get-PercentileValue -Values $stepDurations -Percentile 0.50
    coarseStepDurationP95Ms = Get-PercentileValue -Values $stepDurations -Percentile 0.95
    coarseStepDurationP99Ms = Get-PercentileValue -Values $stepDurations -Percentile 0.99
    queueWaitP50Ms = Get-PercentileValue -Values $queueLag -Percentile 0.50
    queueWaitP95Ms = Get-PercentileValue -Values $queueLag -Percentile 0.95
    queueWaitP99Ms = Get-PercentileValue -Values $queueLag -Percentile 0.99
  }
}

function Get-RealtimeMetrics {
  param([string] $CsvPath)

  if (-not (Test-Path -LiteralPath $CsvPath)) {
    return [ordered]@{ available = $false }
  }

  $rows = @(Import-Csv -LiteralPath $CsvPath)
  $accepted = Get-NumericColumn -Rows $rows -Column "acceptedToEventMs"
  $updated = Get-NumericColumn -Rows $rows -Column "updateToEventMs"
  [ordered]@{
    available = $true
    eventCount = $rows.Count
    timeoutCount = @($rows | Where-Object { $_.status -like "timeout:*" }).Count
    acceptedToEventP50Ms = Get-PercentileValue -Values $accepted -Percentile 0.50
    acceptedToEventP95Ms = Get-PercentileValue -Values $accepted -Percentile 0.95
    updateToEventP50Ms = Get-PercentileValue -Values $updated -Percentile 0.50
    updateToEventP95Ms = Get-PercentileValue -Values $updated -Percentile 0.95
  }
}

function Get-BulkStartMetrics {
  param(
    [string] $SummaryPath,
    [object] $Window
  )

  if (-not (Test-Path -LiteralPath $SummaryPath)) {
    return [ordered]@{ available = $false }
  }

  $summary = Get-Content -LiteralPath $SummaryPath -Raw | ConvertFrom-Json
  $acceptedCount = Get-K6MetricValue $summary "process_start_accepted" "count"
  if ($null -eq $acceptedCount) {
    $acceptedCount = Get-K6MetricValue $summary "process_start_accepted" "rate"
  }
  $durationSeconds = Get-DurationSeconds -Window $Window -Rows @()
  [ordered]@{
    available = $true
    acceptedProcesses = if ($null -ne $acceptedCount) { [double] $acceptedCount } else { $null }
    acceptedProcessesPerSecond = if ($durationSeconds -gt 0 -and $null -ne $acceptedCount) { [double] $acceptedCount / $durationSeconds } else { $null }
    httpDurationP95Ms = Get-K6MetricValue $summary "http_req_duration" "p(95)"
    httpDurationP99Ms = Get-K6MetricValue $summary "http_req_duration" "p(99)"
    httpFailureRate = Get-K6MetricValue $summary "http_req_failed" "rate"
    processStartFailureRate = Get-K6MetricValue $summary "process_start_failed" "rate"
    durationSeconds = $durationSeconds
  }
}

function Get-RuntimeMetrics {
  param([object] $Window)

  $apiRows = Get-CounterRows -CsvPath (Join-Path $resolvedRunRoot "dotnet-counters-api.csv") -Window $Window
  $engineRows = Get-CounterRows -CsvPath (Join-Path $resolvedRunRoot "dotnet-counters-engine.csv") -Window $Window
  $durationSeconds = Get-DurationSeconds -Window $Window -Rows @()
  $apiRu = Get-CounterSum -Rows $apiRows -MetricName "cosmos_request_units_total"
  $engineRu = Get-CounterSum -Rows $engineRows -MetricName "cosmos_request_units_total"
  $finalizations = Get-CounterSum -Rows $engineRows -MetricName "plugin_result_finalizations_total"
  $finalizationFailures = Get-CounterSum -Rows $engineRows -MetricName "plugin_result_finalization_failures_total"

  [ordered]@{
    available = $apiRows.Count -gt 0 -or $engineRows.Count -gt 0
    engine = [ordered]@{
      stepDurationP50Ms = Get-CounterSample -Rows $engineRows -MetricName "process_step_duration_ms" -Percentile "50"
      stepDurationP95Ms = Get-CounterSample -Rows $engineRows -MetricName "process_step_duration_ms" -Percentile "95"
      stepDurationP99Ms = Get-CounterSample -Rows $engineRows -MetricName "process_step_duration_ms" -Percentile "99"
      claimLatencyP95Ms = Get-CounterSample -Rows $engineRows -MetricName "process_step_claim_ms" -Percentile "95"
      queueWaitP95Ms = Get-CounterSample -Rows $engineRows -MetricName "process_step_queue_wait_ms" -Percentile "95"
      pluginStartDelayP95Ms = Get-CounterSample -Rows $engineRows -MetricName "process_step_plugin_start_delay_ms" -Percentile "95"
      pluginExecutionDurationP95Ms = Get-CounterSample -Rows $engineRows -MetricName "plugin_execute_ms" -Percentile "95"
      finalizationDurationP95Ms = Get-CounterSample -Rows $engineRows -MetricName "process_step_finalize_ms" -Percentile "95"
      finalizationThroughputPerSecond = if ($durationSeconds -gt 0 -and $null -ne $finalizations) { $finalizations / $durationSeconds } else { $null }
      finalizationFailures = $finalizationFailures
      cpuMax = Get-CounterMaxByCandidates -Rows $engineRows -MetricNames @("process.cpu.usage", "dotnet.process.cpu.time")
      heapMaxBytes = Get-CounterMaxByCandidates -Rows $engineRows -MetricNames @("dotnet.gc.heap.size", "dotnet.gc.last_collection.heap.size")
      totalAllocatedBytes = Get-CounterMaxByCandidates -Rows $engineRows -MetricNames @("dotnet.gc.heap.total_allocated", "dotnet.gc.heap.total_allocated")
    }
    api = [ordered]@{
      processStartTotalP95Ms = Get-CounterSample -Rows $apiRows -MetricName "process_start_total_ms" -Percentile "95"
      enqueueInitialStepP95Ms = Get-CounterSample -Rows $apiRows -MetricName "process_start_enqueue_initial_step_ms" -Percentile "95"
      cpuMax = Get-CounterMaxByCandidates -Rows $apiRows -MetricNames @("process.cpu.usage", "dotnet.process.cpu.time")
      heapMaxBytes = Get-CounterMaxByCandidates -Rows $apiRows -MetricNames @("dotnet.gc.heap.size", "dotnet.gc.last_collection.heap.size")
      totalAllocatedBytes = Get-CounterMaxByCandidates -Rows $apiRows -MetricNames @("dotnet.gc.heap.total_allocated", "dotnet.gc.heap.total_allocated")
    }
    cosmos = [ordered]@{
      apiRequestUnits = $apiRu
      engineRequestUnits = $engineRu
      totalRequestUnits = if ($null -ne $apiRu -or $null -ne $engineRu) { ([double]($apiRu ?? 0) + [double]($engineRu ?? 0)) } else { $null }
    }
  }
}

function Add-GateOutcome {
  param(
    [System.Collections.Generic.List[object]] $Outcomes,
    [string] $ScenarioKey,
    [string] $Metric,
    [object] $Actual,
    [string] $Operator,
    [object] $Expected,
    [string] $Source
  )

  if ($null -eq $Actual -or $null -eq $Expected) {
    return
  }

  $actualNumber = [double] $Actual
  $expectedNumber = [double] $Expected
  $passed = switch ($Operator) {
    ">=" { $actualNumber -ge $expectedNumber }
    "<=" { $actualNumber -le $expectedNumber }
    default { $true }
  }

  if (-not $passed) {
    $Outcomes.Add([ordered]@{
        scenarioKey = $ScenarioKey
        metric = $Metric
        actual = $actualNumber
        operator = $Operator
        expected = $expectedNumber
        source = $Source
      })
  }
}

function Format-Number($Value, [int] $Digits = 2) {
  if ($null -eq $Value) {
    return "n/a"
  }

  $number = [double] $Value
  if ([double]::IsNaN($number)) {
    return "n/a"
  }

  return $number.ToString("N$Digits")
}

function Format-Ms($Value) {
  if ($null -eq $Value) {
    return "n/a"
  }

  $number = [double] $Value
  if ($number -ge 1000) {
    return ("{0:N2}s" -f ($number / 1000))
  }

  return ("{0:N1}ms" -f $number)
}

function Format-Bytes($Value) {
  if ($null -eq $Value) {
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

  return ("{0:N2} {1}" -f $number, $units[$unitIndex])
}

$scenarioResults = [System.Collections.Generic.List[object]]::new()
$gateFailures = [System.Collections.Generic.List[object]]::new()
$skippedScenarios = [System.Collections.Generic.List[object]]::new()
$baselineByKey = @{}
if ($baseline -and $baseline.scenarios) {
  foreach ($scenario in $baseline.scenarios) {
    $baselineByKey[[string]$scenario.key] = $scenario
  }
}

foreach ($scenario in $manifest.scenarios) {
  $key = [string] $scenario.key
  $enabled = $false -ne (Get-PropertyValue $scenario "enabled" $true)
  if (-not $enabled) {
    $skippedScenarios.Add([ordered]@{
        key = $key
        displayName = [string] $scenario.displayName
        reason = [string] (Get-PropertyValue $scenario "skipReason" "Scenario disabled")
      })
    continue
  }

  if (-not $windowsByScenario.ContainsKey($key)) {
    continue
  }

  $window = $windowsByScenario[$key]
  if ($window.status -eq "skipped") {
    $skippedScenarios.Add([ordered]@{
        key = $key
        displayName = [string] $scenario.displayName
        reason = [string] $window.reason
      })
    continue
  }

  $artifactDir = [string] $window.artifactDir
  $mode = [string] $scenario.mode
  $completionCsv = Join-Path $artifactDir "process-latencies.csv"
  $realtimeCsv = Join-Path $artifactDir "realtime-latencies.csv"
  $k6Summary = Join-Path $artifactDir "k6-summary-admission.json"
  $completionMetrics = Get-CompletionMetrics -CsvPath $completionCsv -Window $window
  $realtimeMetrics = Get-RealtimeMetrics -CsvPath $realtimeCsv
  $bulkMetrics = Get-BulkStartMetrics -SummaryPath $k6Summary -Window $window
  $runtimeMetrics = Get-RuntimeMetrics -Window $window

  $denominatorProcesses = if ($completionMetrics.available -and $completionMetrics.completedProcesses -gt 0) {
    $completionMetrics.completedProcesses
  }
  elseif ($bulkMetrics.available) {
    $bulkMetrics.acceptedProcesses
  }
  else {
    $null
  }
  $denominatorSteps = if ($completionMetrics.available -and $completionMetrics.stepCount -gt 0) {
    $completionMetrics.stepCount
  }
  else {
    $null
  }
  $totalRu = $runtimeMetrics.cosmos.totalRequestUnits
  $ruPerProcess = if ($null -ne $totalRu -and $null -ne $denominatorProcesses -and [double]$denominatorProcesses -gt 0) {
    [double]$totalRu / [double]$denominatorProcesses
  }
  else {
    $null
  }
  $ruPerStep = if ($null -ne $totalRu -and $null -ne $denominatorSteps -and [double]$denominatorSteps -gt 0) {
    [double]$totalRu / [double]$denominatorSteps
  }
  else {
    $null
  }

  $metrics = [ordered]@{
    mode = $mode
    completion = $completionMetrics
    bulkStart = $bulkMetrics
    realtime = $realtimeMetrics
    runtime = $runtimeMetrics
    cosmosRuPerProcess = $ruPerProcess
    cosmosRuPerStep = $ruPerStep
  }

  $thresholds = Get-PropertyValue $scenario "thresholds" $null
  if ($thresholds) {
    Add-GateOutcome $gateFailures $key "successRate" $completionMetrics.successRate ">=" (Get-PropertyValue $thresholds "successRateMin" $null) "threshold"
    Add-GateOutcome $gateFailures $key "processDurationP95Ms" $completionMetrics.processDurationP95Ms "<=" (Get-PropertyValue $thresholds "processDurationP95MsMax" $null) "threshold"
    Add-GateOutcome $gateFailures $key "acceptedProcessesPerSecond" $bulkMetrics.acceptedProcessesPerSecond ">=" (Get-PropertyValue $thresholds "acceptedPerSecondMin" $null) "threshold"
    Add-GateOutcome $gateFailures $key "httpFailureRate" $bulkMetrics.httpFailureRate "<=" (Get-PropertyValue $thresholds "httpFailureRateMax" $null) "threshold"
  }

  if ($baselineByKey.ContainsKey($key)) {
    $baselineScenario = $baselineByKey[$key]
    $budgets = Get-PropertyValue $scenario "regressionBudgets" $manifest.defaultRegressionBudgets
    $latencyBudget = [double](Get-PropertyValue $budgets "latencyIncreasePercent" 25)
    $throughputBudget = [double](Get-PropertyValue $budgets "throughputDropPercent" 15)
    $baselineP95 = Get-PropertyValue $baselineScenario.metrics.completion "processDurationP95Ms" $null
    if ($null -ne $baselineP95) {
      Add-GateOutcome $gateFailures $key "processDurationP95Ms" $completionMetrics.processDurationP95Ms "<=" ([double]$baselineP95 * (1 + $latencyBudget / 100)) "baseline"
    }

    $baselineCompletedPerSec = Get-PropertyValue $baselineScenario.metrics.completion "completedProcessesPerSecond" $null
    if ($null -ne $baselineCompletedPerSec) {
      Add-GateOutcome $gateFailures $key "completedProcessesPerSecond" $completionMetrics.completedProcessesPerSecond ">=" ([double]$baselineCompletedPerSec * (1 - $throughputBudget / 100)) "baseline"
    }

    $baselineAcceptedPerSec = Get-PropertyValue $baselineScenario.metrics.bulkStart "acceptedProcessesPerSecond" $null
    if ($null -ne $baselineAcceptedPerSec) {
      Add-GateOutcome $gateFailures $key "acceptedProcessesPerSecond" $bulkMetrics.acceptedProcessesPerSecond ">=" ([double]$baselineAcceptedPerSec * (1 - $throughputBudget / 100)) "baseline"
    }
  }

  $scenarioResults.Add([ordered]@{
      key = $key
      displayName = [string] $scenario.displayName
      blueprintKey = [string] $scenario.blueprint.key
      profile = $Profile
      config = [ordered]@{
        completionCount = Get-ScenarioSetting $scenario $Profile "completionCount" $null
        completionConcurrency = Get-ScenarioSetting $scenario $Profile "completionConcurrency" $null
        timeoutSeconds = Get-ScenarioSetting $scenario $Profile "timeoutSeconds" $null
        realtimeCount = Get-ScenarioSetting $scenario $Profile "realtimeCount" $null
        collectionItemCount = Get-ScenarioSetting $scenario $Profile "collectionItemCount" $null
      }
      window = $window
      artifactPaths = [ordered]@{
        processLatencyCsv = if (Test-Path -LiteralPath $completionCsv) { $completionCsv } else { $null }
        realtimeLatencyCsv = if (Test-Path -LiteralPath $realtimeCsv) { $realtimeCsv } else { $null }
        k6AdmissionSummaryJson = if (Test-Path -LiteralPath $k6Summary) { $k6Summary } else { $null }
      }
      metrics = $metrics
    })
}

$gateStatus = if ($gateFailures.Count -eq 0) { "Passed" } elseif ($GateMode -eq "Off") { "Recorded" } else { "Failed" }
$observabilitySource = if (-not [string]::IsNullOrWhiteSpace($PrometheusBaseUrl)) {
  "prometheus"
}
elseif ($runConfig -and $runConfig.includeObservability) {
  "dotnet-counters; Prometheus requested but PROMETHEUS_BASE_URL was not provided"
}
else {
  "dotnet-counters"
}

$summary = [ordered]@{
  schemaVersion = 1
  generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
  runRoot = $resolvedRunRoot
  profile = $Profile
  gateMode = $GateMode
  gateStatus = $gateStatus
  observabilitySource = $observabilitySource
  baselinePath = if ([string]::IsNullOrWhiteSpace($BaselinePath)) { $null } else { $BaselinePath }
  runConfig = $runConfig
  scenarioWindowsPath = $windowsPath
  scenarios = @($scenarioResults)
  skippedScenarios = @($skippedScenarios)
  gateFailures = @($gateFailures)
}

$jsonPath = Join-Path $resolvedRunRoot "throughput-scoreboard.json"
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$mdLines = [System.Collections.Generic.List[string]]::new()
$mdLines.Add("# WorkflowBuilder Throughput Scoreboard")
$mdLines.Add("")
$mdLines.Add("- Run: ``$resolvedRunRoot``")
$mdLines.Add("- Profile: $Profile")
$mdLines.Add("- Gate mode: $GateMode")
$mdLines.Add("- Gate status: $gateStatus")
$mdLines.Add("- Observability source: $observabilitySource")
$mdLines.Add("- Generated: $($summary.generatedAtUtc)")
$mdLines.Add("")
$mdLines.Add("## Scenario Matrix")
$mdLines.Add("")
$mdLines.Add("| Scenario | Blueprint | Accepted/s | Completed/s | Steps/s | Process P50 | Process P95 | Process P99 | Step P95 | Queue P95 | Claim P95 | Plugin start P95 | Plugin exec P95 | Finalize P95 | RU/process | RU/step | Realtime P95 |")
$mdLines.Add("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
foreach ($scenario in $scenarioResults) {
  $completion = $scenario.metrics.completion
  $bulk = $scenario.metrics.bulkStart
  $runtime = $scenario.metrics.runtime
  $realtime = $scenario.metrics.realtime
  $acceptedPerSecond = if ($bulk.available) { $bulk.acceptedProcessesPerSecond } else { $completion.acceptedProcessesPerSecond }
  $stepP95 = if ($null -ne $runtime.engine.stepDurationP95Ms) { $runtime.engine.stepDurationP95Ms } else { $completion.coarseStepDurationP95Ms }
  $queueP95 = if ($null -ne $runtime.engine.queueWaitP95Ms) { $runtime.engine.queueWaitP95Ms } else { $completion.queueWaitP95Ms }
  $mdLines.Add((
      "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} | {15} | {16} |" -f
      $scenario.displayName,
      $scenario.blueprintKey,
      (Format-Number $acceptedPerSecond),
      (Format-Number $completion.completedProcessesPerSecond),
      (Format-Number $completion.stepsPerSecond),
      (Format-Ms $completion.processDurationP50Ms),
      (Format-Ms $completion.processDurationP95Ms),
      (Format-Ms $completion.processDurationP99Ms),
      (Format-Ms $stepP95),
      (Format-Ms $queueP95),
      (Format-Ms $runtime.engine.claimLatencyP95Ms),
      (Format-Ms $runtime.engine.pluginStartDelayP95Ms),
      (Format-Ms $runtime.engine.pluginExecutionDurationP95Ms),
      (Format-Ms $runtime.engine.finalizationDurationP95Ms),
      (Format-Number $scenario.metrics.cosmosRuPerProcess),
      (Format-Number $scenario.metrics.cosmosRuPerStep),
      (Format-Ms $realtime.updateToEventP95Ms)))
}
$mdLines.Add("")

$mdLines.Add("## Runtime Counters")
$mdLines.Add("")
$mdLines.Add("| Scenario | API CPU max | API heap max | API allocated | Engine CPU max | Engine heap max | Engine allocated | Finalizations/s | Finalization failures |")
$mdLines.Add("|---|---:|---:|---:|---:|---:|---:|---:|---:|")
foreach ($scenario in $scenarioResults) {
  $runtime = $scenario.metrics.runtime
  $mdLines.Add((
      "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} |" -f
      $scenario.displayName,
      (Format-Number $runtime.api.cpuMax),
      (Format-Bytes $runtime.api.heapMaxBytes),
      (Format-Bytes $runtime.api.totalAllocatedBytes),
      (Format-Number $runtime.engine.cpuMax),
      (Format-Bytes $runtime.engine.heapMaxBytes),
      (Format-Bytes $runtime.engine.totalAllocatedBytes),
      (Format-Number $runtime.engine.finalizationThroughputPerSecond),
      (Format-Number $runtime.engine.finalizationFailures)))
}
$mdLines.Add("")

if ($skippedScenarios.Count -gt 0) {
  $mdLines.Add("## Skipped")
  $mdLines.Add("")
  foreach ($skipped in $skippedScenarios) {
    $mdLines.Add("- $($skipped.displayName): $($skipped.reason)")
  }
  $mdLines.Add("")
}

$mdLines.Add("## Gates")
$mdLines.Add("")
if ($gateFailures.Count -eq 0) {
  $mdLines.Add("- No threshold or baseline regression breach was detected.")
}
else {
  foreach ($failure in $gateFailures) {
    $mdLines.Add("- $($failure.scenarioKey) ``$($failure.metric)`` = $(Format-Number $failure.actual) must be $($failure.operator) $(Format-Number $failure.expected) ($($failure.source)).")
  }
}
$mdLines.Add("")
$mdLines.Add("## Artifacts")
$mdLines.Add("")
$mdLines.Add("- Machine-readable summary: ``throughput-scoreboard.json``")
$mdLines.Add("- Scenario windows: ``scenario-windows.json``")
$mdLines.Add("- Per-scenario artifacts: ``scenarios/<scenarioKey>/``")
$mdLines.Add("- Runtime counters: ``dotnet-counters-api.csv`` and ``dotnet-counters-engine.csv`` when available")
$mdLines.Add("")

$mdPath = Join-Path $resolvedRunRoot "throughput-scoreboard.md"
$mdLines | Set-Content -LiteralPath $mdPath -Encoding UTF8

if ($gateFailures.Count -gt 0) {
  foreach ($failure in $gateFailures) {
    Write-Warning "$($failure.scenarioKey) $($failure.metric)=$($failure.actual) expected $($failure.operator) $($failure.expected) ($($failure.source))"
  }
}

Write-Host "[scoreboard] wrote $jsonPath"
Write-Host "[scoreboard] wrote $mdPath"

if ($GateMode -eq "Fail" -and $gateFailures.Count -gt 0) {
  exit 1
}

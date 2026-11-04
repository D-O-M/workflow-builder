[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)] [string] $ResourceGroup,
  [Parameter(Mandatory = $true)] [datetime] $StartUtc,
  [Parameter(Mandatory = $true)] [datetime] $EndUtc,
  [Parameter(Mandatory = $true)] [string] $OutputPath
)

$ErrorActionPreference = "Stop"

if ($EndUtc.ToUniversalTime() -le $StartUtc.ToUniversalTime()) {
  throw "EndUtc must be later than StartUtc."
}
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
  throw "Azure CLI is required to collect Azure NFT metrics."
}

function Read-MetricPayload($Payload) {
  $metrics = @($Payload.value | Where-Object { $null -ne $_ })
  if ($metrics.Count -ne 1) {
    throw "Azure Monitor did not return the requested metric."
  }

  $metric = $metrics[0]
  if (-not [string]::IsNullOrWhiteSpace($metric.errorCode) -and $metric.errorCode -ne "Success") {
    throw "Azure Monitor metric error '$($metric.errorCode)': $($metric.errorMessage)"
  }

  return $metric
}

$metricSets = @{
  "Microsoft.App/containerApps" = @(
    @{ Name = "UsageNanoCores"; Aggregation = "Average" },
    @{ Name = "WorkingSetBytes"; Aggregation = "Average" },
    @{ Name = "Replicas"; Aggregation = "Maximum" },
    # RestartCount is cumulative per replica, so retain its maximum rather than sum repeated samples.
    @{ Name = "RestartCount"; Aggregation = "Maximum" }
  )
  "Microsoft.ServiceBus/namespaces" = @(
    @{ Name = "IncomingMessages"; Aggregation = "Total" },
    @{ Name = "OutgoingMessages"; Aggregation = "Total" },
    @{ Name = "ActiveMessages"; Aggregation = "Maximum" },
    @{ Name = "DeadletteredMessages"; Aggregation = "Maximum" },
    @{ Name = "ThrottledRequests"; Aggregation = "Total" },
    @{ Name = "ServerErrors"; Aggregation = "Total" }
  )
  "Microsoft.DocumentDB/databaseAccounts" = @(
    @{ Name = "TotalRequests"; Aggregation = "Count" },
    @{ Name = "TotalRequestUnits"; Aggregation = "Total" },
    @{ Name = "NormalizedRUConsumption"; Aggregation = "Maximum" }
  )
  "Microsoft.Sql/servers/databases" = @(
    @{ Name = "cpu_percent"; Aggregation = "Maximum" },
    @{ Name = "dtu_consumption_percent"; Aggregation = "Maximum" },
    @{ Name = "workers_percent"; Aggregation = "Maximum" },
    @{ Name = "sessions_percent"; Aggregation = "Maximum" }
  )
}

$resourceJson = & az resource list `
  --resource-group $ResourceGroup `
  --only-show-errors `
  --output json
if ($LASTEXITCODE -ne 0) {
  throw "Unable to list Azure resources in '$ResourceGroup'."
}

$resources = @($resourceJson | ConvertFrom-Json | Where-Object {
    $metricSets.ContainsKey([string] $_.type) -and
    -not ($_.type -eq "Microsoft.Sql/servers/databases" -and $_.name.EndsWith("/master", [StringComparison]::OrdinalIgnoreCase))
  })
$results = [System.Collections.Generic.List[object]]::new()
$startText = $StartUtc.ToUniversalTime().ToString("O")
$endText = $EndUtc.ToUniversalTime().ToString("O")

foreach ($resource in $resources | Sort-Object type, name) {
  foreach ($metric in $metricSets[[string] $resource.type]) {
    try {
      $metricJson = & az monitor metrics list `
        --resource $resource.id `
        --metric $metric.Name `
        --aggregation $metric.Aggregation `
        --interval PT1M `
        --start-time $startText `
        --end-time $endText `
        --only-show-errors `
        --output json
      if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI returned exit code $LASTEXITCODE."
      }

      $payload = Read-MetricPayload ($metricJson | ConvertFrom-Json)
      $results.Add([ordered]@{
          resourceType = $resource.type
          resourceName = $resource.name
          metric = $metric.Name
          aggregation = $metric.Aggregation
          unit = $payload.unit
          timeseries = @($payload.timeseries | Where-Object { $null -ne $_ })
          error = $null
        })
    }
    catch {
      $results.Add([ordered]@{
          resourceType = $resource.type
          resourceName = $resource.name
          metric = $metric.Name
          aggregation = $metric.Aggregation
          unit = $null
          timeseries = @()
          error = $_.Exception.Message
        })
    }
  }

  if ($resource.type -eq "Microsoft.DocumentDB/databaseAccounts") {
    try {
      $throttleJson = & az monitor metrics list `
        --resource $resource.id `
        --metric TotalRequests `
        --aggregation Count `
        --filter "StatusCode eq '429'" `
        --interval PT1M `
        --start-time $startText `
        --end-time $endText `
        --only-show-errors `
        --output json
      if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI returned exit code $LASTEXITCODE."
      }

      $payload = Read-MetricPayload ($throttleJson | ConvertFrom-Json)
      $results.Add([ordered]@{
          resourceType = $resource.type
          resourceName = $resource.name
          metric = "TotalRequests429"
          aggregation = "Count"
          unit = $payload.unit
          timeseries = @($payload.timeseries | Where-Object { $null -ne $_ })
          error = $null
        })
    }
    catch {
      $results.Add([ordered]@{
          resourceType = $resource.type
          resourceName = $resource.name
          metric = "TotalRequests429"
          aggregation = "Count"
          unit = $null
          timeseries = @()
          error = $_.Exception.Message
        })
    }
  }
}

$document = [ordered]@{
  resourceGroup = $ResourceGroup
  startUtc = $startText
  endUtc = $endText
  interval = "PT1M"
  collectedUtc = (Get-Date).ToUniversalTime().ToString("O")
  metrics = $results
}

$directory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$document | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "[nft] Azure metrics written to $OutputPath"

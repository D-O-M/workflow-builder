<#
.SYNOPSIS
  Runs WorkflowBuilder non-functional tests locally or against a deployed target.

.DESCRIPTION
  Builds deterministic NFT plugin bundles, starts the AppHost in a detached lean profile,
  seeds NFT blueprints/bundles, runs bounded load probes, collects metrics, writes a report,
  and stops the AppHost at the end. Remote mode uses only the deployed services.
#>

[CmdletBinding()]
param(
  [ValidateSet("Local", "Remote")]
  [string] $Target = "Local",

  [string] $ApiBaseUrl = "",
  [string] $AccessToken = $env:WORKFLOWBUILDER_NFT_ACCESS_TOKEN,
  [string] $AzureResourceGroup = "",
  [string] $AzureStorageAccountName = "",
  [string] $AzurePluginsContainerName = "plugins",
  [string] $PluginRuntimeIdentifier = "",
  [switch] $SkipAssetSeeding,
  [ValidateRange(0, 1000)]
  [int] $WarmupCount = 1,

  [ValidateSet("Smoke", "Standard", "Staircase", "Capacity", "Scoreboard")]
  [string] $Profile = "Smoke",

  [ValidateSet("ceiling-lane", "product-lane", "both")]
  [string] $Lane = "ceiling-lane",

  [string] $NftBlueprintKey = "nft-ceiling-fanout-join",
  [string[]] $ScenarioKeys = @(),
  [ValidateSet("Off", "Warn", "Fail")]
  [string] $GateMode = "Warn",
  [string] $BaselinePath = "",
  [switch] $IncludeObservability,
  [switch] $NoBuild,
  [switch] $RebuildBundles,
  [string] $BundleCacheRoot = "",
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
  [int] $PluginRuntimeResultFinalizationQueueCapacity = 1024,
  [ValidateSet("Wait")]
  [string] $PluginRuntimeResultFinalizationQueueFullMode = "Wait",
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
$remoteTarget = $Target -eq "Remote"
if ([string]::IsNullOrWhiteSpace($ApiBaseUrl)) {
  if ($remoteTarget) {
    throw "-ApiBaseUrl is required when -Target Remote is used."
  }

  $ApiBaseUrl = "http://localhost:5180"
}
$ApiBaseUrl = $ApiBaseUrl.TrimEnd("/")
if ([string]::IsNullOrWhiteSpace($PluginRuntimeIdentifier)) {
  $PluginRuntimeIdentifier = if ($remoteTarget) { "linux-x64" } else { "win-x64" }
}
if ($remoteTarget -and -not $SkipAssetSeeding) {
  if ([string]::IsNullOrWhiteSpace($AzureResourceGroup)) {
    throw "-AzureResourceGroup is required for remote asset seeding."
  }
  if ([string]::IsNullOrWhiteSpace($AzureStorageAccountName)) {
    throw "-AzureStorageAccountName is required for remote asset seeding."
  }
}
$apiHeaders = @{}
if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
  $apiHeaders["Authorization"] = "Bearer $AccessToken"
}
$appHostPath = Join-Path $repoRoot "Backend.AppHost\Backend.AppHost.csproj"
$probeProjectPath = Join-Path $repoRoot "PerformanceTests\WorkflowBuilder.PerformanceProbe\WorkflowBuilder.PerformanceProbe.csproj"
$scoreboardManifestPath = Join-Path $repoRoot "PerformanceTests\scoreboard-scenarios.json"
$azureMetricsScriptPath = Join-Path $repoRoot "PerformanceTests\scripts\Collect-AzureNftMetrics.ps1"
$topologyCatalogPath = Join-Path $repoRoot "BuildScripts\workflowbuilder-topology.json"
if ([string]::IsNullOrWhiteSpace($BundleCacheRoot)) {
  $BundleCacheRoot = Join-Path $repoRoot ".artifacts\nft-bundle-cache"
}
$topology = Get-Content -LiteralPath $topologyCatalogPath -Raw | ConvertFrom-Json
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

function Invoke-ApiRequest {
  param(
    [Parameter(Mandatory = $true)] [string] $Path,
    [ValidateSet("Get", "Post")] [string] $Method = "Get",
    [string] $BodyPath = ""
  )

  $arguments = @{
    Uri = "$ApiBaseUrl/$($Path.TrimStart('/'))"
    Method = $Method
    Headers = $apiHeaders
    TimeoutSec = 30
  }
  if (-not [string]::IsNullOrWhiteSpace($BodyPath)) {
    $arguments["InFile"] = $BodyPath
    $arguments["ContentType"] = "application/json"
  }

  return Invoke-RestMethod @arguments
}

function Split-BlueprintKeys([string] $BlueprintKeys) {
  return @($BlueprintKeys -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Split-ScenarioKeys([string[]] $Keys) {
  return @($Keys |
    ForEach-Object { $_ -split "," } |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ })
}

function Get-ObjectPropertyValue($Object, [string] $Name, $Fallback = $null) {
  if ($null -eq $Object) {
    return $Fallback
  }

  if ($Object.PSObject.Properties.Name -contains $Name) {
    return $Object.$Name
  }

  return $Fallback
}

function Get-ScoreboardManifest {
  return Get-Content -LiteralPath $scoreboardManifestPath -Raw | ConvertFrom-Json
}

function Get-ScoreboardProfileSettings($Manifest) {
  $settings = Get-ObjectPropertyValue $Manifest.profiles $Profile $null
  if ($null -eq $settings) {
    throw "Scoreboard profile '$Profile' was not found in $scoreboardManifestPath."
  }

  return $settings
}

function Get-ScenarioProfileSettings($Scenario) {
  $profiles = Get-ObjectPropertyValue $Scenario "profiles" $null
  if ($null -eq $profiles) {
    return $null
  }

  return Get-ObjectPropertyValue $profiles $Profile $null
}

function Get-ScenarioSetting($Manifest, $Scenario, [string] $Name, $Fallback = $null) {
  $scenarioProfile = Get-ScenarioProfileSettings $Scenario
  $scenarioValue = Get-ObjectPropertyValue $scenarioProfile $Name $null
  if ($null -ne $scenarioValue) {
    return $scenarioValue
  }

  $profileSettings = Get-ScoreboardProfileSettings $Manifest
  $profileValue = Get-ObjectPropertyValue $profileSettings $Name $null
  if ($null -ne $profileValue) {
    return $profileValue
  }

  return $Fallback
}

function Test-ScenarioEnabled($Scenario) {
  if ($false -eq (Get-ObjectPropertyValue $Scenario "enabled" $true)) {
    return $false
  }

  $scenarioProfile = Get-ScenarioProfileSettings $Scenario
  return $false -ne (Get-ObjectPropertyValue $scenarioProfile "enabled" $true)
}

function Resolve-ScoreboardScenarios($Manifest) {
  $requestedKeys = Split-ScenarioKeys $ScenarioKeys
  $profileSettings = Get-ScoreboardProfileSettings $Manifest
  $selectedKeys = if ($requestedKeys.Count -gt 0) {
    $requestedKeys
  }
  else {
    @($profileSettings.scenarioKeys)
  }

  $byKey = @{}
  foreach ($scenario in $Manifest.scenarios) {
    $byKey[[string]$scenario.key] = $scenario
  }

  $selected = [System.Collections.Generic.List[object]]::new()
  foreach ($key in $selectedKeys) {
    if (-not $byKey.ContainsKey($key)) {
      throw "Scoreboard scenario '$key' was not found in $scoreboardManifestPath."
    }

    $scenario = $byKey[$key]
    if (-not (Test-ScenarioEnabled $scenario)) {
      Write-Step "Scoreboard scenario '$key' is disabled; it will be recorded as skipped."
    }

    $selected.Add($scenario)
  }

  return @($selected)
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
    $response = Invoke-WebRequest -Uri "$ApiBaseUrl/health" -Headers $apiHeaders -UseBasicParsing -TimeoutSec 5
    return $response.StatusCode -ge 200 -and $response.StatusCode -lt 300
  }
  catch {
    return $false
  }
}

function Wait-ForApi {
  $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    if (-not $remoteTarget -and $appHostProcess -and $appHostProcess.HasExited) {
      throw "AppHost exited early with code $($appHostProcess.ExitCode). See $appHostStdout and $appHostStderr."
    }

    if (Test-ApiHealth) {
      return
    }

    Start-Sleep -Seconds 5
  }

  throw "Timed out waiting for API health at $ApiBaseUrl/health."
}

function Wait-ForBlueprint([string] $BlueprintKey) {
  $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    try {
      $blueprints = Invoke-ApiRequest -Path "api/blueprints"
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

function Get-BlueprintTaskRequirements([string] $BlueprintDirectory) {
  $requirements = foreach ($file in Get-ChildItem -LiteralPath $BlueprintDirectory -Filter *.blueprint.json -File -Recurse) {
    $blueprint = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($node in $blueprint.tasks) {
      if ([string]::IsNullOrWhiteSpace($node.taskDefinitionName)) {
        continue
      }

      [pscustomobject]@{
        name = $node.taskDefinitionName.Trim()
        major = [int] $node.allowedMajor
        minor = [int] $node.allowedMinor
      }
    }
  }

  return @($requirements | Sort-Object name, major, minor -Unique)
}

function Wait-ForTaskDefinitions([object[]] $Requirements) {
  if ($Requirements.Count -eq 0) {
    return
  }

  $missing = @($Requirements | ForEach-Object { "$($_.name) $($_.major).$($_.minor).*" })
  $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    try {
      $tasks = Invoke-ApiRequest -Path "Tasks"
      $missing = @(foreach ($requirement in $Requirements) {
        $definition = $tasks | Where-Object { $_.name -eq $requirement.name } | Select-Object -First 1
        $matchingVersions = @($definition.versions | Where-Object {
          $version = $null
          [Version]::TryParse([string] $_.version, [ref] $version) -and
            $version.Major -eq $requirement.major -and
            $version.Minor -eq $requirement.minor
        })
        if ($matchingVersions.Count -eq 0) {
          "$($requirement.name) $($requirement.major).$($requirement.minor).*"
        }
      })
      if ($missing.Count -eq 0) {
        return
      }
    }
    catch {
      Write-Verbose "Task poll failed: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds 5
  }

  throw "Timed out waiting for task definition version lanes: $($missing -join ', ')."
}

function Copy-BlueprintSeeds {
  if ($Lane -eq "ceiling-lane" -or $Lane -eq "both") {
    Copy-Item -Path (Join-Path $repoRoot "PerformanceTests\Blueprints\*.json") -Destination $blueprintRoot -Force
    if ($scoreboardMode) {
      Invoke-CheckedCommand -FilePath "pwsh" -Arguments @(
        "-NoLogo",
        "-NoProfile",
        "-File",
        (Join-Path $repoRoot "PerformanceTests\scripts\New-ThroughputScoreboardBlueprints.ps1"),
        "-ManifestPath",
        $scoreboardManifestPath,
        "-OutputDir",
        $blueprintRoot
      )
    }
  }

  if ($Lane -eq "product-lane" -or $Lane -eq "both") {
    Copy-Item -Path (Join-Path $repoRoot "SeedData\Blueprints\*.json") -Destination $blueprintRoot -Force
  }
}

function Get-NftBundleCacheKey {
  $inputs = @(
    "PerformanceTests\Plugins",
    "Backend.Plugins.Communication",
    "Backend.Plugins.Harness",
    "Backend.PluginAbi",
    "Backend.Plugins.Configuration\Abstractions",
    "BuildScripts\bundle-plugins.ps1",
    "Directory.Packages.props",
    "Directory.Build.props",
    "Directory.Build.targets",
    "global.json"
  )

  $files = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
  foreach ($input in $inputs) {
    $path = Join-Path $repoRoot $input
    if (-not (Test-Path -LiteralPath $path)) {
      continue
    }

    $item = Get-Item -LiteralPath $path
    if ($item.PSIsContainer) {
      Get-ChildItem -LiteralPath $item.FullName -File -Recurse |
        Where-Object {
          $_.FullName -notmatch "\\(bin|obj|_bundles)\\"
        } |
        ForEach-Object { $files.Add($_) }
    }
    else {
      $files.Add($item)
    }
  }

  $rootPrefix = (Resolve-Path $repoRoot).Path.TrimEnd("\") + "\"
  $manifest = @($files |
    Sort-Object FullName |
    ForEach-Object {
      $relativePath = $_.FullName.Substring($rootPrefix.Length).Replace("\", "/")
      $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
      "$relativePath|$hash"
    })

  $payload = [string]::Join("`n", @($manifest) + "runtimeIdentifier|$PluginRuntimeIdentifier")
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
  $hashBytes = [System.Security.Cryptography.SHA256]::HashData($bytes)
  return -join ($hashBytes | ForEach-Object { $_.ToString("x2") })
}

function Copy-CachedNftBundles {
  param(
    [string] $CachePath,
    [string] $CacheKey,
    [bool] $CacheHit
  )

  $bundles = @(Get-ChildItem -LiteralPath $CachePath -Filter *.zip -File -ErrorAction SilentlyContinue)
  if ($bundles.Count -eq 0) {
    throw "NFT bundle cache '$CachePath' does not contain any bundle zips."
  }

  $bundles | Copy-Item -Destination $bundleRoot -Force
  [ordered]@{
    cacheKey = $CacheKey
    cacheHit = $CacheHit
    cachePath = $CachePath
    bundleCount = $bundles.Count
  } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot "nft-bundle-cache.json") -Encoding UTF8
}

function Build-NftBundles {
  $cacheKey = Get-NftBundleCacheKey
  $cachePath = Join-Path (Join-Path $BundleCacheRoot "performance") $cacheKey
  $cachedBundles = @(Get-ChildItem -LiteralPath $cachePath -Filter *.zip -File -ErrorAction SilentlyContinue)

  if (-not $RebuildBundles -and $cachedBundles.Count -gt 0) {
    Write-Step "Using cached NFT bundles $cacheKey from $cachePath."
    Copy-CachedNftBundles -CachePath $cachePath -CacheKey $cacheKey -CacheHit $true
    return
  }

  Write-Step "Building NFT bundles for cache key $cacheKey."
  New-Item -ItemType Directory -Force -Path $cachePath | Out-Null
  Get-ChildItem -LiteralPath $cachePath -Filter *.zip -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

  Invoke-CheckedCommand -FilePath "pwsh" -Arguments @(
    "-NoLogo",
    "-NoProfile",
    "-File",
    (Join-Path $repoRoot "BuildScripts\bundle-plugins.ps1"),
    "-RootDir",
    (Join-Path $repoRoot "PerformanceTests\Plugins"),
    "-OutputDir",
    $cachePath,
    "-Configuration",
    "Release",
    "-RuntimeIdentifier",
    $PluginRuntimeIdentifier,
    "-Overwrite",
    "-Minify"
  )

  Copy-CachedNftBundles -CachePath $cachePath -CacheKey $cacheKey -CacheHit $false
}

function Get-BundleRuntimeIdentifier([string] $Path) {
  $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
  try {
    $metadataEntry = $archive.GetEntry("bundle.meta.json")
    if ($null -eq $metadataEntry) {
      return $null
    }

    $reader = [System.IO.StreamReader]::new($metadataEntry.Open())
    try {
      return ($reader.ReadToEnd() | ConvertFrom-Json).rid
    }
    finally {
      $reader.Dispose()
    }
  }
  finally {
    $archive.Dispose()
  }
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
      $PluginRuntimeIdentifier,
      "-Overwrite",
      "-Minify"
    )
  }

  $bundles = @(Get-ChildItem -LiteralPath $productionBundleRoot -Filter *.zip -File -ErrorAction SilentlyContinue |
    Where-Object { (Get-BundleRuntimeIdentifier $_.FullName) -eq $PluginRuntimeIdentifier })
  if ($bundles.Count -eq 0) {
    throw "Product lane requested, but no $PluginRuntimeIdentifier production bundles were found in $productionBundleRoot. Re-run with -BuildProductBundles."
  }

  $bundles | Copy-Item -Destination $bundleRoot -Force
}

function Publish-NftBundlesToAzure {
  if ($SkipAssetSeeding) {
    Write-Step "Skipping remote plugin bundle upload."
    return
  }

  if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI is required for remote NFT asset seeding."
  }

  & az account show --only-show-errors --output none
  if ($LASTEXITCODE -ne 0) {
    throw "Azure CLI is not logged in. Run 'az login' before the remote NFT."
  }

  foreach ($bundle in Get-ChildItem -LiteralPath $bundleRoot -Filter *.zip -File | Sort-Object Name) {
    $existsJson = & az storage blob exists `
      --account-name $AzureStorageAccountName `
      --container-name $AzurePluginsContainerName `
      --name $bundle.Name `
      --auth-mode key `
      --only-show-errors `
      --output json
    if ($LASTEXITCODE -ne 0) {
      throw "Unable to check whether plugin bundle '$($bundle.Name)' exists in Azure Storage."
    }

    $exists = ($existsJson | ConvertFrom-Json).exists
    if ($exists) {
      Write-Step "Remote plugin bundle $($bundle.Name) already exists; keeping the registered version."
      continue
    }

    Write-Step "Uploading remote plugin bundle $($bundle.Name)."
    & az storage blob upload `
      --account-name $AzureStorageAccountName `
      --container-name $AzurePluginsContainerName `
      --name $bundle.Name `
      --file $bundle.FullName `
      --auth-mode key `
      --overwrite false `
      --only-show-errors `
      --output none
    if ($LASTEXITCODE -ne 0) {
      throw "Failed to upload plugin bundle '$($bundle.Name)'."
    }
  }
}

function Import-NftBlueprintsToRemoteApi {
  if ($SkipAssetSeeding) {
    Write-Step "Skipping remote blueprint import."
    return
  }

  $blueprintFiles = @(Get-ChildItem -LiteralPath $blueprintRoot -Filter *.blueprint.json -File -Recurse | Sort-Object FullName)
  foreach ($blueprintFile in $blueprintFiles) {
    Write-Step "Importing remote blueprint seed $($blueprintFile.Name)."
    $null = Invoke-ApiRequest -Path "api/blueprints/import" -Method Post -BodyPath $blueprintFile.FullName
  }
}

function Write-AzureDeploymentMetadata {
  if (-not $remoteTarget -or [string]::IsNullOrWhiteSpace($AzureResourceGroup)) {
    return
  }

  try {
    $apps = @(& az containerapp list `
      --resource-group $AzureResourceGroup `
      --only-show-errors `
      --output json | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) {
      throw "Azure CLI returned exit code $LASTEXITCODE."
    }

    $metadata = [ordered]@{
      capturedUtc = (Get-Date).ToUniversalTime().ToString("O")
      resourceGroup = $AzureResourceGroup
      containerApps = @($apps | Sort-Object name | ForEach-Object {
          [ordered]@{
            name = $_.name
            image = $_.properties.template.containers[0].image
            revision = $_.properties.latestReadyRevisionName
            cpu = $_.properties.template.containers[0].resources.cpu
            memory = $_.properties.template.containers[0].resources.memory
            minReplicas = if ($null -eq $_.properties.template.scale.minReplicas) { 0 } else { $_.properties.template.scale.minReplicas }
            maxReplicas = $_.properties.template.scale.maxReplicas
            scaleRules = @($_.properties.template.scale.rules | ForEach-Object { $_.name })
          }
        })
    }
    $metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runRoot "azure-deployment.json") -Encoding UTF8
  }
  catch {
    Write-Warning "Unable to capture Azure deployment metadata: $($_.Exception.Message)"
  }
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
    "Nft__MinimalServices" = "true"
    "Nft__PluginRuntime__MaxConcurrentResultFinalizations" = $PluginRuntimeMaxConcurrentResultFinalizations.ToString()
    "Nft__PluginRuntime__ResultFinalizationQueueCapacity" = $PluginRuntimeResultFinalizationQueueCapacity.ToString()
    "Nft__PluginRuntime__ResultFinalizationQueueFullMode" = $PluginRuntimeResultFinalizationQueueFullMode
    "Nft__ProcessStepQueue__MaxConcurrentMessages" = $ProcessStepQueueMaxConcurrentMessages.ToString()
    "Nft__ProcessStepQueue__PrefetchCount" = $ProcessStepQueuePrefetchCount.ToString()
    "Nft__ProcessStepQueue__MaxAutoLockRenewalMinutes" = $ProcessStepQueueMaxAutoLockRenewalMinutes.ToString()
    "SignalR__Enabled" = "false"
    "ProgressBroadcaster__Enabled" = "false"
    "Dispatcher__Enabled" = "true"
    "ProjectionWorker__Enabled" = "true"
    "Maintenance__Enabled" = "false"
    "PluginDetector__Enabled" = "false"
    "Projections__SynchronousEngineUpdatesEnabled" = "false"
    "PluginSeeding__Enabled" = "true"
    "PluginSeeding__BundlesRoot" = $bundleRoot
    "BlueprintSeeding__Enabled" = "true"
    "BlueprintSeeding__BlueprintsRoot" = $blueprintRoot
    "AzureCosmos__ResourceName" = $topology.cosmos.defaultResourceName
    "AzureCosmos__ConnectionName" = $topology.cosmos.defaultDatabaseName
    "AzureCosmos__DatabaseName" = $topology.cosmos.defaultDatabaseName
    "AzureCosmos__PersistData" = "false"
    "AzureStorage__ResourceName" = $topology.storage.defaultResourceName
    "AzureStorage__BlobContainers__Plugins__ResourceName" = $topology.storage.plugins.resourceName
    "AzureStorage__BlobContainers__Plugins__ContainerName" = $topology.storage.plugins.containerName
    "AzureStorage__BlobContainers__Artifacts__ResourceName" = $topology.storage.artifacts.resourceName
    "AzureStorage__BlobContainers__Artifacts__ContainerName" = $topology.storage.artifacts.containerName
    "AzureStorage__PersistData" = "false"
    "SqlServer__PersistData" = "false"
    "AzureServiceBus__ResourceName" = $topology.serviceBus.defaultResourceName
    "AzureServiceBus__Queues__ProcessStepQueue__ResourceName" = $topology.serviceBus.processStepQueue.resourceName
    "AzureServiceBus__Queues__ProcessStepQueue__ConnectionName" = $topology.serviceBus.processStepQueue.connectionName
    "AzureServiceBus__Queues__ProcessStepQueue__Name" = $topology.serviceBus.processStepQueue.queueName
    "AzureServiceBus__Queues__PluginControlQueue__ResourceName" = $topology.serviceBus.pluginControlQueue.resourceName
    "AzureServiceBus__Queues__PluginControlQueue__ConnectionName" = $topology.serviceBus.pluginControlQueue.connectionName
    "AzureServiceBus__Queues__PluginControlQueue__Name" = $topology.serviceBus.pluginControlQueue.queueName
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
    [string] $PayloadPath = "",
    [hashtable] $EnvironmentOverrides = @{}
  )

  $scriptPath = Join-Path $repoRoot "PerformanceTests\k6\$ScriptName"
  $summaryPath = Join-Path $runRoot $SummaryName
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $summaryPath) | Out-Null
  $logPath = Join-Path (Split-Path -Parent $summaryPath) "$([System.IO.Path]::GetFileNameWithoutExtension($ScriptName)).log"
  $envValues = @{
    "API_BASE_URL" = $ApiBaseUrl
    "BLUEPRINT_KEY" = $BlueprintKey
    "NFT_PROFILE" = $Profile.ToLowerInvariant()
    "K6_SUMMARY_PATH" = $summaryPath
    "ACCESS_TOKEN" = $AccessToken
  }

  foreach ($entry in $EnvironmentOverrides.GetEnumerator()) {
    $envValues[$entry.Key] = [string] $entry.Value
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

  $containerApiUri = [UriBuilder]::new($ApiBaseUrl)
  if (-not $remoteTarget -and $containerApiUri.Uri.IsLoopback) {
    $containerApiUri.Host = "host.docker.internal"
  }
  $containerEnv = @{
    "API_BASE_URL" = $containerApiUri.Uri.AbsoluteUri.TrimEnd('/')
    "BLUEPRINT_KEY" = $BlueprintKey
    "NFT_PROFILE" = $Profile.ToLowerInvariant()
    "K6_SUMMARY_PATH" = (Convert-ToContainerPath $summaryPath)
    "ACCESS_TOKEN" = $AccessToken
  }

  foreach ($entry in $EnvironmentOverrides.GetEnumerator()) {
    $containerEnv[$entry.Key] = [string] $entry.Value
  }

  if ($PayloadPath) {
    $containerEnv["PROCESS_PAYLOAD_JSON"] = (Convert-ToContainerPath $PayloadPath)
  }

  $dockerArgs = @("run", "--rm")
  foreach ($entry in $containerEnv.GetEnumerator()) {
    if ($entry.Key -eq "ACCESS_TOKEN") {
      $dockerArgs += @("-e", "ACCESS_TOKEN")
    }
    else {
      $dockerArgs += @("-e", "$($entry.Key)=$($entry.Value)")
    }
  }
  $dockerArgs += @("-v", "${repoRoot}:/repo", "grafana/k6:latest", "run", (Convert-ToContainerPath $scriptPath))

  $previousToken = [Environment]::GetEnvironmentVariable("ACCESS_TOKEN")
  try {
    [Environment]::SetEnvironmentVariable("ACCESS_TOKEN", $containerEnv["ACCESS_TOKEN"])
    & docker @dockerArgs 2>&1 | Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0) {
      Add-ScenarioFailure "Docker k6 script $ScriptName crossed thresholds or failed with exit code $LASTEXITCODE."
    }
  }
  finally {
    [Environment]::SetEnvironmentVariable("ACCESS_TOKEN", $previousToken)
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
    [string] $PayloadPath = "",
    [string[]] $ExtraArguments = @()
  )

  $outputPath = Join-Path $runRoot $OutputName
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

  if ((Split-BlueprintKeys $BlueprintKey).Count -gt 1) {
    $arguments += @("--blueprint-keys", $BlueprintKey)
  }
  else {
    $arguments += @("--blueprint-key", $BlueprintKey)
  }

  if ($PayloadPath) {
    $arguments += @("--payload", $PayloadPath)
  }
  if ($ExtraArguments.Count -gt 0) {
    $arguments += $ExtraArguments
  }

  $previousToken = [Environment]::GetEnvironmentVariable("WORKFLOWBUILDER_NFT_ACCESS_TOKEN")
  try {
    [Environment]::SetEnvironmentVariable("WORKFLOWBUILDER_NFT_ACCESS_TOKEN", $AccessToken)
    Invoke-CheckedCommand -FilePath "dotnet" -Arguments $arguments
  }
  finally {
    [Environment]::SetEnvironmentVariable("WORKFLOWBUILDER_NFT_ACCESS_TOKEN", $previousToken)
  }
}

function Invoke-RemoteWarmup {
  if (-not $remoteTarget -or $WarmupCount -le 0) {
    return
  }

  Write-Step "Warming the deployed API, Dispatcher, Engine, and plugin runtime with $WarmupCount completed process(es)."
  $warmupBlueprintKey = if ($Lane -eq "product-lane") { $productBlueprintKey } else { "nft-linear-single-step" }
  $warmupPayload = if ($Lane -eq "product-lane") { Join-Path $repoRoot "PerformanceTests\payloads\showcase-town-weekend.json" } else { "" }
  Invoke-PerformanceProbe `
    -Command "completion" `
    -OutputName "warmup-process-latencies.csv" `
    -BlueprintKey $warmupBlueprintKey `
    -PayloadPath $warmupPayload `
    -Count $WarmupCount `
    -Concurrency 1 `
    -TimeoutSeconds 180

  $warmupPath = Join-Path $runRoot "warmup-process-latencies.csv"
  $warmupRows = @(Import-Csv -LiteralPath $warmupPath)
  $completedCount = @($warmupRows | Where-Object { $_.status -eq "Completed" }).Count
  if ($warmupRows.Count -ne $WarmupCount -or $completedCount -ne $WarmupCount) {
    throw "Remote warmup completed $completedCount of $WarmupCount process(es). Inspect $warmupPath before measuring performance."
  }
}

function Collect-AzureMetrics {
  param(
    [datetime] $StartUtc,
    [datetime] $EndUtc
  )

  if (-not $remoteTarget -or [string]::IsNullOrWhiteSpace($AzureResourceGroup)) {
    return
  }

  try {
    Invoke-CheckedCommand -FilePath "pwsh" -Arguments @(
      "-NoLogo",
      "-NoProfile",
      "-File",
      $azureMetricsScriptPath,
      "-ResourceGroup",
      $AzureResourceGroup,
      "-StartUtc",
      $StartUtc.ToUniversalTime().ToString("O"),
      "-EndUtc",
      $EndUtc.ToUniversalTime().ToString("O"),
      "-OutputPath",
      (Join-Path $runRoot "azure-metrics.json")
    )
  }
  catch {
    Write-Warning "Azure metric collection failed; scenario results remain available: $($_.Exception.Message)"
  }
}

function Write-RunConfig {
  $config = [ordered]@{
    timestamp = $timestamp
    target = $Target
    profile = $Profile
    lane = $Lane
    nftBlueprintKey = $nftBlueprintKey
    includeObservability = [bool] $IncludeObservability
    minimalServices = -not $remoteTarget
    disabledAppHostServices = if ($remoteTarget) { @() } else {
      @(
        "backend-signalr",
        "progress-broadcaster",
        "backend-maintenance",
        "plugin-detector"
      )
    }
    apiBaseUrl = $ApiBaseUrl
    azureResourceGroup = if ($remoteTarget) { $AzureResourceGroup } else { $null }
    azureStorageAccountName = if ($remoteTarget) { $AzureStorageAccountName } else { $null }
    pluginRuntimeIdentifier = $PluginRuntimeIdentifier
    assetSeedingEnabled = -not ($remoteTarget -and $SkipAssetSeeding)
    warmupCount = if ($remoteTarget) { $WarmupCount } else { 0 }
    authentication = if ([string]::IsNullOrWhiteSpace($AccessToken)) { "none" } else { "bearer" }
    gitSha = (& git rev-parse HEAD)
    dotnet = (& dotnet --version)
    docker = if (Get-Command docker -ErrorAction SilentlyContinue) { (& docker --version) } else { "<missing>" }
    k6 = if (Get-Command k6 -ErrorAction SilentlyContinue) { (& k6 version) } else { "docker fallback or missing" }
    processor = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)
    logicalProcessors = (Get-CimInstance Win32_Processor | Measure-Object NumberOfLogicalProcessors -Sum).Sum
    memoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    completionOnly = [bool] $CompletionOnly
    scoreboardMode = [bool] $scoreboardMode
    scenarioKeys = @(Split-ScenarioKeys $ScenarioKeys)
    gateMode = $GateMode
    baselinePath = $BaselinePath
    scoreboardManifestPath = $scoreboardManifestPath
    rebuildBundles = [bool] $RebuildBundles
    bundleCacheRoot = $BundleCacheRoot
    ceilingCompletionCount = $CeilingCompletionCount
    ceilingCompletionConcurrency = $CeilingCompletionConcurrency
    capacityStart = $CapacityStart
    capacityMax = $CapacityMax
    capacityTimeoutSeconds = $CapacityTimeoutSeconds
    pluginRuntimeMaxConcurrentResultFinalizations = if ($remoteTarget) { $null } else { $PluginRuntimeMaxConcurrentResultFinalizations }
    pluginRuntimeResultFinalizationQueueCapacity = if ($remoteTarget) { $null } else { $PluginRuntimeResultFinalizationQueueCapacity }
    pluginRuntimeResultFinalizationQueueFullMode = if ($remoteTarget) { $null } else { $PluginRuntimeResultFinalizationQueueFullMode }
    processStepQueueMaxConcurrentMessages = if ($remoteTarget) { $null } else { $ProcessStepQueueMaxConcurrentMessages }
    processStepQueuePrefetchCount = if ($remoteTarget) { $null } else { $ProcessStepQueuePrefetchCount }
    processStepQueueMaxAutoLockRenewalMinutes = if ($remoteTarget) { $null } else { $ProcessStepQueueMaxAutoLockRenewalMinutes }
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
  $blueprints = Invoke-ApiRequest -Path "api/blueprints"
  $match = @($blueprints | Where-Object { $_.key -eq $BlueprintKey }) | Select-Object -First 1
  if (-not $match) {
    throw "Blueprint '$BlueprintKey' was not found."
  }

  return [int] $match.id
}

function Get-BlueprintIdMap([string] $BlueprintKeys) {
  $blueprints = Invoke-ApiRequest -Path "api/blueprints"
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
    $detail = Invoke-ApiRequest -Path "api/blueprints/$BlueprintId/processes/$ProcessId"
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

function Get-ScenarioArtifactName([string] $ScenarioKey, [string] $FileName) {
  return "scenarios\$ScenarioKey\$FileName"
}

function Invoke-ScoreboardScenario($Manifest, $Scenario) {
  $key = [string] $Scenario.key
  $displayName = [string] $Scenario.displayName
  $blueprintKey = [string] $Scenario.blueprint.key
  $mode = [string] $Scenario.mode
  $scenarioDir = Join-Path $runRoot "scenarios\$key"
  New-Item -ItemType Directory -Force -Path $scenarioDir | Out-Null

  $window = [ordered]@{
    key = $key
    displayName = $displayName
    mode = $mode
    blueprintKey = $blueprintKey
    artifactDir = $scenarioDir
    startUtc = $null
    endUtc = $null
    status = "pending"
    reason = $null
  }

  if (-not (Test-ScenarioEnabled $Scenario)) {
    $window.status = "skipped"
    $window.reason = [string] (Get-ObjectPropertyValue $Scenario "skipReason" "Scenario disabled for this profile")
    return [pscustomobject] $window
  }

  $window.startUtc = (Get-Date).ToUniversalTime().ToString("O")
  try {
    Write-Step "Scoreboard scenario '$key' ($displayName) using blueprint '$blueprintKey'."
    if ($mode -eq "completion") {
      $count = [int] (Get-ScenarioSetting $Manifest $Scenario "completionCount" 3)
      $concurrency = [int] (Get-ScenarioSetting $Manifest $Scenario "completionConcurrency" 1)
      $timeoutSeconds = [int] (Get-ScenarioSetting $Manifest $Scenario "timeoutSeconds" 60)
      $realtimeCount = [int] (Get-ScenarioSetting $Manifest $Scenario "realtimeCount" 0)
      $collectionItemCount = [int] (Get-ScenarioSetting $Manifest $Scenario "collectionItemCount" (Get-ObjectPropertyValue $Scenario.blueprint "itemCount" 0))
      $extraArguments = @()
      if ($collectionItemCount -gt 0) {
        $extraArguments += @("--collection-item-count", $collectionItemCount.ToString())
      }

      & {
        Invoke-PerformanceProbe `
          -Command "completion" `
          -OutputName (Get-ScenarioArtifactName $key "process-latencies.csv") `
          -BlueprintKey $blueprintKey `
          -Count $count `
          -Concurrency $concurrency `
          -TimeoutSeconds $timeoutSeconds `
          -ExtraArguments $extraArguments
      } | Out-Host

      if (-not $CompletionOnly -and $realtimeCount -gt 0) {
        & {
          Invoke-PerformanceProbe `
            -Command "realtime" `
            -OutputName (Get-ScenarioArtifactName $key "realtime-latencies.csv") `
            -BlueprintKey $blueprintKey `
            -Count $realtimeCount `
            -Concurrency 1 `
            -TimeoutSeconds ([Math]::Min($timeoutSeconds, 120)) `
            -ExtraArguments $extraArguments
        } | Out-Host
      }
    }
    elseif ($mode -eq "bulk-start") {
      $admissionVus = [int] (Get-ScenarioSetting $Manifest $Scenario "admissionVus" 2)
      $admissionDuration = [string] (Get-ScenarioSetting $Manifest $Scenario "admissionDuration" "20s")
      & {
        Invoke-K6Script `
          -ScriptName "admission.js" `
          -SummaryName (Get-ScenarioArtifactName $key "k6-summary-admission.json") `
          -BlueprintKey $blueprintKey `
          -EnvironmentOverrides @{
            K6_VUS = $admissionVus.ToString()
            K6_DURATION = $admissionDuration
            K6_THINK_TIME_SECONDS = "0"
          }
      } | Out-Host
    }
    else {
      throw "Unknown scoreboard scenario mode '$mode'."
    }

    $window.status = "completed"
  }
  catch {
    $window.status = "failed"
    $window.reason = $_.Exception.Message
    Add-ScenarioFailure "Scoreboard scenario '$key' failed: $($_.Exception.Message)"
  }
  finally {
    $window.endUtc = (Get-Date).ToUniversalTime().ToString("O")
  }

  return [pscustomobject] $window
}

function Invoke-ScoreboardMatrix {
  $manifest = Get-ScoreboardManifest
  $scenarios = Resolve-ScoreboardScenarios $manifest
  $windows = [System.Collections.Generic.List[object]]::new()

  foreach ($scenario in $scenarios) {
    $windows.Add((Invoke-ScoreboardScenario $manifest $scenario))
  }

  $windows.ToArray() |
    ConvertTo-Json -Depth 10 -AsArray |
    Set-Content -LiteralPath (Join-Path $runRoot "scenario-windows.json") -Encoding UTF8

  $summaryArgs = @(
    "-NoLogo",
    "-NoProfile",
    "-File",
    (Join-Path $repoRoot "PerformanceTests\scripts\Write-ThroughputScoreboard.ps1"),
    "-RunRoot",
    $runRoot,
    "-ManifestPath",
    $scoreboardManifestPath,
    "-Profile",
    $Profile,
    "-GateMode",
    $GateMode
  )
  if (-not [string]::IsNullOrWhiteSpace($BaselinePath)) {
    $summaryArgs += @("-BaselinePath", $BaselinePath)
  }

  $prometheusBaseUrl = [Environment]::GetEnvironmentVariable("PROMETHEUS_BASE_URL")
  if ($IncludeObservability -and -not [string]::IsNullOrWhiteSpace($prometheusBaseUrl)) {
    $summaryArgs += @("-PrometheusBaseUrl", $prometheusBaseUrl)
  }

  & pwsh @summaryArgs
  if ($LASTEXITCODE -ne 0) {
    if ($GateMode -eq "Fail") {
      throw "Scoreboard gates failed. See throughput-scoreboard.md in $runRoot."
    }

    Add-ScenarioFailure "Scoreboard summarization exited with code $LASTEXITCODE."
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
    "# WorkflowBuilder NFT Run",
    "",
    "- Timestamp: $timestamp",
    "- Target: $Target",
    "- Profile: $Profile",
    "- Lane: $Lane",
    "- NFT blueprint key: $($runConfig.nftBlueprintKey)",
    ($(if ($remoteTarget) { "- Deployed service configuration: see azure-deployment.json when AzureResourceGroup is supplied." } else {
      "- Observability included: $([bool]$IncludeObservability)"
      "- Plugin runtime max concurrent result finalizations: $($runConfig.pluginRuntimeMaxConcurrentResultFinalizations)"
      "- Plugin result finalization queue capacity: $($runConfig.pluginRuntimeResultFinalizationQueueCapacity)"
      "- Process-step queue max concurrent messages: $($runConfig.processStepQueueMaxConcurrentMessages)"
      "- Process-step queue prefetch count: $($runConfig.processStepQueuePrefetchCount)"
      "- Process-step queue max auto-lock renewal: $($runConfig.processStepQueueMaxAutoLockRenewalMinutes) minute(s)"
    }) -join [Environment]::NewLine),
    "- API base URL: $ApiBaseUrl",
    "- Bundles staged: $bundleCount",
    "- Blueprints staged: $blueprintCount",
    "- Git SHA: $($runConfig.gitSha)",
    "- Injector .NET SDK: $($runConfig.dotnet)",
    "- Injector processor: $($runConfig.processor)",
    "- Injector logical processors: $($runConfig.logicalProcessors)",
    "- Injector memory: $([Math]::Round([double]$runConfig.memoryBytes / 1GB, 2)) GiB",
    ($(if (-not $remoteTarget) {
      "- AppHost stdout: $appHostStdout"
      "- AppHost stderr: $appHostStderr"
    }) -join [Environment]::NewLine),
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
$runStartedUtc = (Get-Date).ToUniversalTime()
$scoreboardMode = $Profile -eq "Scoreboard" -or (Split-ScenarioKeys $ScenarioKeys).Count -gt 0
$scoreboardManifest = if ($scoreboardMode) { Get-ScoreboardManifest } else { $null }
$scoreboardScenarios = if ($scoreboardMode) { Resolve-ScoreboardScenarios $scoreboardManifest } else { @() }

try {
  Write-RunConfig
  if (-not $remoteTarget) {
    Stop-StaleAspireAppHost
    if (Test-ApiHealth) {
      throw "Backend.API is already responding on $ApiBaseUrl. Stop the existing app before running the NFT proof."
    }
  }

  if (-not $NoBuild) {
    Invoke-CheckedCommand -FilePath "dotnet" -Arguments @("build", $probeProjectPath, "-c", "Release")
    if (-not $remoteTarget) {
      Invoke-CheckedCommand -FilePath "dotnet" -Arguments @("build", (Join-Path $repoRoot "Backend.API\Backend.API.csproj"), "-c", "Release")
      Invoke-CheckedCommand -FilePath "dotnet" -Arguments @("build", (Join-Path $repoRoot "Backend.Engine\Backend.Engine.csproj"), "-c", "Release")
      Invoke-CheckedCommand -FilePath "dotnet" -Arguments @("build", $appHostPath, "-c", "Release")
    }
  }

  Copy-BlueprintSeeds
  if (-not ($remoteTarget -and $SkipAssetSeeding)) {
    Build-NftBundles
    if ($Lane -eq "product-lane" -or $Lane -eq "both") {
      Copy-ProductBundles
    }
  }

  $countersAvailable = $false
  if ($remoteTarget) {
    Publish-NftBundlesToAzure
    Write-AzureDeploymentMetadata
  }
  else {
    $countersAvailable = Ensure-DotnetCounters
    $dockerStatsProcess = Start-DockerStatsCapture

    $appHost = Start-AppHost
    $appHostProcess = $appHost.Process
    $appHostStdout = $appHost.Stdout
    $appHostStderr = $appHost.Stderr
  }

  Write-Step "Waiting for API health and seeded metadata."
  Wait-ForApi
  if (-not ($remoteTarget -and $SkipAssetSeeding)) {
    Wait-ForTaskDefinitions -Requirements @(Get-BlueprintTaskRequirements -BlueprintDirectory $blueprintRoot)
  }
  if ($remoteTarget) {
    Import-NftBlueprintsToRemoteApi
  }
  if ($Lane -eq "ceiling-lane" -or $Lane -eq "both") {
    $blueprintKeysToWait = if ($scoreboardMode) {
      @($scoreboardScenarios |
        Where-Object { Test-ScenarioEnabled $_ } |
        ForEach-Object { [string] $_.blueprint.key } |
        Sort-Object -Unique)
    }
    else {
      Split-BlueprintKeys $nftBlueprintKey
    }

    foreach ($blueprintKey in $blueprintKeysToWait) {
      Wait-ForBlueprint $blueprintKey
    }
  }
  if ($Lane -eq "product-lane" -or $Lane -eq "both") {
    Wait-ForBlueprint $productBlueprintKey
  }

  Invoke-RemoteWarmup

  if ($countersAvailable) {
    $counterProcesses += Start-CounterCapture -ProjectName "Backend.API" -OutputName "dotnet-counters-api.csv"
    $counterProcesses += Start-CounterCapture -ProjectName "Backend.Engine" -OutputName "dotnet-counters-engine.csv"
  }

  if ($scoreboardMode -and ($Lane -eq "ceiling-lane" -or $Lane -eq "both")) {
    Invoke-ScoreboardMatrix
  }
  elseif ($Lane -eq "ceiling-lane" -or $Lane -eq "both") {
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
  Collect-AzureMetrics -StartUtc $runStartedUtc -EndUtc ((Get-Date).ToUniversalTime())

  foreach ($process in $counterProcesses | Where-Object { $_ }) {
    try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
  }

  if ($dockerStatsProcess) {
    try { Stop-Process -Id $dockerStatsProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
  }

  if (-not $remoteTarget -and $appHostProcess -and -not $KeepAppHost) {
    Stop-ProcessTree -ProcessId $appHostProcess.Id
    Stop-StaleAspireAppHost
  }

  if (-not $remoteTarget -and $KeepAppHost) {
    Write-Step "Keeping AppHost running with PID $($appHostProcess.Id)."
  }
}

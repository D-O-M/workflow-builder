[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)] [string] $ManifestPath,
  [Parameter(Mandatory = $true)] [string] $OutputDir
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$jsonOptionsDepth = 100
$laneTaskDefinitions = @(
  "NftLinearLaneOnePlugin",
  "NftLinearLaneTwoPlugin",
  "NftLinearLaneThreePlugin",
  "NftLinearLaneFourPlugin",
  "NftLinearLaneFivePlugin"
)

function New-DeterministicGuid([string] $Text) {
  $md5 = [System.Security.Cryptography.MD5]::Create()
  try {
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text))
    return [Guid]::new($bytes)
  }
  finally {
    $md5.Dispose()
  }
}

function New-Mapping([string] $Source, [string] $Target) {
  [ordered]@{
    sourceProperty = $Source
    targetProperty = $Target
  }
}

function New-Connection(
  [string] $ChannelName,
  [Guid] $Destination,
  [object[]] $Mappings) {
  [ordered]@{
    channelName = $ChannelName
    connectedTaskKey = $Destination
    anchor = 1
    side = "left"
    position = 62
    propertyMappings = @($Mappings)
  }
}

function Normalize-Connections([object[]] $OutgoingConnections) {
  if ($null -eq $OutgoingConnections) {
    return @()
  }

  return @($OutgoingConnections | Where-Object { $null -ne $_ })
}

function New-PluginNode(
  [Guid] $TaskKey,
  [string] $TaskDefinitionName,
  [int] $X,
  [int] $Y,
  [object[]] $OutgoingConnections,
  [hashtable] $DesignTimeInputOverrides = $null) {
  $node = [ordered]@{
    taskKey = $TaskKey
    taskDefinitionName = $TaskDefinitionName
    allowedMajor = 1
    allowedMinor = 0
    position = [ordered]@{ x = $X; y = $Y }
  }

  if ($DesignTimeInputOverrides) {
    $node.designTimeInputOverrides = $DesignTimeInputOverrides
  }

  $node.outgoingConnections = @(Normalize-Connections $OutgoingConnections)
  return $node
}

function New-CollectionIteratorNode(
  [Guid] $TaskKey,
  [int] $X,
  [int] $Y,
  [object[]] $OutgoingConnections) {
  [ordered]@{
    taskKey = $TaskKey
    nodeKind = "CollectionIterator"
    collectionIteratorItemKind = "String"
    allowedMajor = 0
    allowedMinor = 0
    position = [ordered]@{ x = $X; y = $Y }
    outgoingConnections = @(Normalize-Connections $OutgoingConnections)
  }
}

function New-SeedToLaneMappings {
  @(
    New-Mapping "CorrelationKey" "CorrelationKey"
    New-Mapping "PayloadToken" "PayloadToken"
    New-Mapping "SeedScore" "SeedScore"
  )
}

function New-LaneToLaneMappings {
  @(
    New-Mapping "CorrelationKey" "CorrelationKey"
    New-Mapping "LaneDigest" "PayloadToken"
    New-Mapping "LaneScore" "SeedScore"
  )
}

function Write-Blueprint([object] $Blueprint, [string] $Key) {
  $path = Join-Path $OutputDir "$Key.blueprint.json"
  $Blueprint |
    ConvertTo-Json -Depth $jsonOptionsDepth |
    Set-Content -LiteralPath $path -Encoding UTF8
  Write-Host "[scoreboard] generated $path"
}

function New-LinearBlueprint($Scenario) {
  $key = [string] $Scenario.blueprint.key
  $nodeCount = [int] $Scenario.blueprint.nodeCount
  if ($nodeCount -lt 1) {
    throw "Linear scenario '$($Scenario.key)' must have nodeCount >= 1."
  }

  $nodeIds = @(0..($nodeCount - 1) | ForEach-Object { New-DeterministicGuid "${key}:node:$_" })
  $nodes = [System.Collections.Generic.List[object]]::new()
  $seedConnections = if ($nodeCount -gt 1) {
    @(
      New-Connection "Context" $nodeIds[1] (New-SeedToLaneMappings)
    )
  }
  else {
    @()
  }

  $nodes.Add((New-PluginNode $nodeIds[0] "NftSeedPlugin" 120 260 $seedConnections))

  for ($index = 1; $index -lt $nodeCount; $index++) {
    $taskDefinitionName = $laneTaskDefinitions[($index - 1) % $laneTaskDefinitions.Count]
    $connections = if ($index -lt $nodeCount - 1) {
      @(
        New-Connection "Result" $nodeIds[$index + 1] (New-LaneToLaneMappings)
      )
    }
    else {
      @()
    }

    $nodes.Add((New-PluginNode `
      $nodeIds[$index] `
      $taskDefinitionName `
      (120 + $index * 320) `
      260 `
      $connections `
      @{
        LaneName = "linear-$index"
        LaneWeight = 1 + ($index % 5)
        DelayMs = 0
      }))
  }

  [ordered]@{
    schemaVersion = 3
    definitionKey = $key
    displayName = [string] $Scenario.displayName
    startingTaskKey = $nodeIds[0]
    tasks = @($nodes)
  }
}

function New-FanOutBlueprint($Scenario) {
  $key = [string] $Scenario.blueprint.key
  $fanOut = [int] $Scenario.blueprint.fanOut
  if ($fanOut -lt 1) {
    throw "Fan-out scenario '$($Scenario.key)' must have fanOut >= 1."
  }

  $seedId = New-DeterministicGuid "${key}:seed"
  $laneIds = @(0..($fanOut - 1) | ForEach-Object { New-DeterministicGuid "${key}:lane:$_" })
  $nodes = [System.Collections.Generic.List[object]]::new()
  $seedConnections = @($laneIds | ForEach-Object {
      New-Connection "Context" $_ (New-SeedToLaneMappings)
    })

  $nodes.Add((New-PluginNode $seedId "NftSeedPlugin" 120 ([Math]::Max(120, [int]($fanOut * 8))) $seedConnections))

  for ($index = 0; $index -lt $fanOut; $index++) {
    $taskDefinitionName = $laneTaskDefinitions[$index % $laneTaskDefinitions.Count]
    $row = $index % 20
    $column = [Math]::Floor($index / 20)
    $nodes.Add((New-PluginNode `
      $laneIds[$index] `
      $taskDefinitionName `
      (520 + [int]$column * 260) `
      (60 + $row * 48) `
      @() `
      @{
        LaneName = "fanout-$index"
        LaneWeight = 1 + ($index % 5)
        DelayMs = 0
      }))
  }

  [ordered]@{
    schemaVersion = 3
    definitionKey = $key
    displayName = [string] $Scenario.displayName
    startingTaskKey = $seedId
    tasks = @($nodes)
  }
}

function New-CollectionIteratorBlueprint($Scenario) {
  $key = [string] $Scenario.blueprint.key
  $seedId = New-DeterministicGuid "${key}:collection-seed"
  $iteratorId = New-DeterministicGuid "${key}:iterator"
  $sinkId = New-DeterministicGuid "${key}:item-sink"

  $seed = New-PluginNode `
    $seedId `
    "NftCollectionSeedPlugin" `
    120 `
    260 `
    @(
      New-Connection "Collection" $iteratorId @(
        New-Mapping "Items" "items"
      )
    )

  $iterator = New-CollectionIteratorNode `
    $iteratorId `
    520 `
    260 `
    @(
      New-Connection "Item" $sinkId @(
        New-Mapping "item" "Item"
      )
    )

  $sink = New-PluginNode `
    $sinkId `
    "NftItemSinkPlugin" `
    920 `
    260 `
    @() `
    @{
      DelayMs = 0
    }

  [ordered]@{
    schemaVersion = 3
    definitionKey = $key
    displayName = [string] $Scenario.displayName
    startingTaskKey = $seedId
    tasks = @($seed, $iterator, $sink)
  }
}

foreach ($scenario in $manifest.scenarios) {
  if ($false -eq $scenario.enabled) {
    continue
  }

  if ($scenario.blueprint.source -ne "generated") {
    continue
  }

  $blueprint = switch ([string] $scenario.blueprint.generator) {
    "linear" { New-LinearBlueprint $scenario }
    "fanout" { New-FanOutBlueprint $scenario }
    "collectionIterator" { New-CollectionIteratorBlueprint $scenario }
    default { throw "Unknown scoreboard blueprint generator '$($scenario.blueprint.generator)' for scenario '$($scenario.key)'." }
  }

  Write-Blueprint $blueprint ([string] $scenario.blueprint.key)
}

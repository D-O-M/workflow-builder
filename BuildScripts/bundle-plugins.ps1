<#
.SYNOPSIS
  Publishes and bundles executable plugin projects into canonical versioned ZIPs.

.DESCRIPTION
  For each executable SDK-style plugin project under -RootDir, this script:
    1) Publishes the project
    2) Reads the generated contract artifacts from obj\<Configuration>\<TFM>\plugin-contract:
         manifest.json + schemas.json
    3) Compares that contract with the latest canonical bundle for the same plugin
    4) Updates the project <Version> automatically:
         - first canonical bundle keeps the declared version
         - unchanged contract bumps patch
         - changed contract bumps minor and resets patch to 0
         - major is manual only; a higher declared major is respected as-is
    5) Republishes if the version changed
    6) Creates a canonical bundle:
         <OutputDir>\<ProjectName>_<Version>.zip
         ├─ manifest.json
         ├─ schemas.json OR schemas\*.json
         └─ app\

  "Executable plugin" detection heuristics (any true => treated as plugin):
    - <OutputType>Exe</OutputType> OR <OutputType>WinExe</OutputType> in the csproj
    - OR presence of Program.cs

.PARAMETER RootDir
  Folder to search for .csproj files (recursively).

.PARAMETER OutputDir
  Where canonical plugin ZIPs are written. Defaults to "<RootDir>\_bundles".

.PARAMETER Configuration
  Build/publish configuration. Default: Release

.PARAMETER RuntimeIdentifier
  RID when publishing (for example win-x64, linux-x64, linux-musl-x64, osx-arm64).
  If omitted, framework-dependent publish.

.PARAMETER SelfContained
  Switch: publish self-contained (requires -RuntimeIdentifier). Default: false.
  Framework-dependent bundles are the canonical default because plugins run under WorkflowBuilder Engine hosts.

.PARAMETER SingleFile
  Switch: publish as single file. Default: false

.PARAMETER ReadyToRun
  Switch: publish ReadyToRun. Default: false

.PARAMETER CompositeR2R
  Switch: composite ReadyToRun (requires ReadyToRun). Default: false

.PARAMETER Minify
  Switch: minify manifest/schemas JSON in the bundle. Default: false

.PARAMETER MaxBundleSizeMiB
  Maximum compressed bundle size before the script fails. Set to 0 to disable.
  Default: 30 MiB for normal framework-dependent bundles.
  The default limit is skipped for explicit -SelfContained or -SingleFile builds unless this parameter is provided.

.PARAMETER Overwrite
  Switch: overwrite an already-existing canonical bundle for the final resolved version.
  Default: false

.PARAMETER ProjectFilter
  Optional glob to filter project file names (for example "*Plugin*.csproj").

.EXAMPLE
  .\bundle-plugins.ps1 -RootDir .\Backend.Plugins -OutputDir .\Backend.Plugins\_bundles `
    -Configuration Release -RuntimeIdentifier win-x64 -Overwrite
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)] [string] $RootDir,
  [string] $OutputDir,
  [string] $Configuration = "Release",
  [string] $RuntimeIdentifier,
  [switch] $SelfContained,
  [switch] $SingleFile,
  [switch] $ReadyToRun,
  [switch] $CompositeR2R,
  [switch] $Minify,
  [switch] $Overwrite,
  [double] $MaxBundleSizeMiB = 30,
  [string] $ProjectFilter
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$enforceBundleSizeLimit =
  $MaxBundleSizeMiB -gt 0 -and
  (-not ($SelfContained -or $SingleFile) -or $PSBoundParameters.ContainsKey('MaxBundleSizeMiB'))

function Get-AbsolutePath([string] $path, [string] $basePath = (Get-Location).Path) {
  if ([string]::IsNullOrWhiteSpace($path)) {
    throw "Path cannot be empty."
  }

  if ([System.IO.Path]::IsPathRooted($path)) {
    return [System.IO.Path]::GetFullPath($path)
  }

  return [System.IO.Path]::GetFullPath((Join-Path $basePath $path))
}

function Get-NodeText($document, [string] $localName) {
  try {
    $node = $document.SelectSingleNode("//*[local-name()='$localName']")
    if ($null -ne $node) {
      return $node.InnerText.Trim()
    }
  }
  catch { }

  return $null
}

function Get-CsprojInfo([string] $csprojPath) {
  $raw = Get-Content -LiteralPath $csprojPath -Raw -Encoding UTF8
  [xml]$xml = $raw

  $projectDir = Split-Path -Parent $csprojPath
  $projectName = [System.IO.Path]::GetFileNameWithoutExtension($csprojPath)
  $targetFramework = Get-NodeText $xml 'TargetFramework'
  $targetFrameworks = Get-NodeText $xml 'TargetFrameworks'
  if ([string]::IsNullOrWhiteSpace($targetFramework) -and -not [string]::IsNullOrWhiteSpace($targetFrameworks)) {
    $targetFramework = ($targetFrameworks -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[0]
  }

  $outputType = Get-NodeText $xml 'OutputType'
  if ($null -eq $outputType) {
    $outputType = ''
  }

  $versionText = Get-NodeText $xml 'Version'
  $hasProgram = Test-Path -LiteralPath (Join-Path $projectDir 'Program.cs')

  return @{
    CsprojPath      = $csprojPath
    ProjectDir      = $projectDir
    ProjectName     = $projectName
    TargetFramework = $targetFramework
    OutputType      = $outputType
    HasProgramCs    = $hasProgram
    Version         = $versionText
  }
}

function Save-XmlDocument([xml] $xml, [string] $path) {
  $settings = [System.Xml.XmlWriterSettings]::new()
  $settings.Indent = $true
  $settings.IndentChars = "    "
  $settings.NewLineChars = "`r`n"
  $settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace
  $settings.OmitXmlDeclaration = $true
  $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
  $settings.Encoding = $utf8NoBom

  $writer = [System.Xml.XmlWriter]::Create($path, $settings)
  try {
    $xml.Save($writer)
  }
  finally {
    $writer.Dispose()
  }

  $content = [System.IO.File]::ReadAllText($path, $utf8NoBom)
  if (-not $content.EndsWith("`r`n")) {
    [System.IO.File]::WriteAllText($path, $content + "`r`n", $utf8NoBom)
  }
}

function Set-ProjectVersion([string] $csprojPath, [Version] $version) {
  [xml]$xml = Get-Content -LiteralPath $csprojPath -Raw -Encoding UTF8
  $projectNode = $xml.SelectSingleNode("/*[local-name()='Project']")
  if ($null -eq $projectNode) {
    throw "Could not load project XML for $csprojPath."
  }

  $propertyGroup = $projectNode.SelectSingleNode("*[local-name()='PropertyGroup']")
  if ($null -eq $propertyGroup) {
    $propertyGroup = $xml.CreateElement("PropertyGroup")
    $projectNode.AppendChild($propertyGroup) | Out-Null
  }

  $versionNode = $propertyGroup.SelectSingleNode("*[local-name()='Version']")
  if ($null -eq $versionNode) {
    $versionNode = $xml.CreateElement("Version")
    $propertyGroup.AppendChild($versionNode) | Out-Null
  }

  $versionNode.InnerText = $version.ToString(3)
  Save-XmlDocument $xml $csprojPath
}

function Is-ExecutableProject($info) {
  $outputType = ''
  if ($null -ne $info.OutputType) {
    $outputType = $info.OutputType.ToString()
  }

  switch ($outputType.ToLowerInvariant()) {
    'exe' { return $true }
    'winexe' { return $true }
  }

  return [bool]$info.HasProgramCs
}

function Get-VersionOrThrow([string] $versionText, [string] $projectName) {
  if ([string]::IsNullOrWhiteSpace($versionText)) {
    throw "Project $projectName is missing an explicit <Version>."
  }

  $parsedVersion = $null
  if (-not [Version]::TryParse($versionText, [ref] $parsedVersion)) {
    throw "Project $projectName has an invalid <Version> value '$versionText'."
  }

  return [Version]::new($parsedVersion.Major, $parsedVersion.Minor, (Get-VersionPatch $parsedVersion))
}

function Get-VersionPatch([Version] $version) {
  if ($version.Build -lt 0) {
    return 0
  }

  return $version.Build
}

function Compare-VersionCore([Version] $left, [Version] $right) {
  $leftParts = @($left.Major, $left.Minor, (Get-VersionPatch $left))
  $rightParts = @($right.Major, $right.Minor, (Get-VersionPatch $right))

  for ($index = 0; $index -lt $leftParts.Length; $index++) {
    if ($leftParts[$index] -lt $rightParts[$index]) { return -1 }
    if ($leftParts[$index] -gt $rightParts[$index]) { return 1 }
  }

  return 0
}

function Compute-PublishDir($info, [string] $configuration, [string] $rid) {
  if ([string]::IsNullOrWhiteSpace($info.TargetFramework)) {
    throw "TargetFramework not specified in project $($info.ProjectName).csproj"
  }

  if ([string]::IsNullOrWhiteSpace($rid)) {
    return Join-Path $info.ProjectDir "bin\$configuration\$($info.TargetFramework)\publish"
  }

  return Join-Path $info.ProjectDir "bin\$configuration\$($info.TargetFramework)\$rid\publish"
}

function Invoke-PluginPublish($info) {
  if (($SelfContained -or $SingleFile -or $ReadyToRun -or $CompositeR2R) -and [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    throw "RuntimeIdentifier is required when using -SelfContained, -SingleFile, -ReadyToRun, or -CompositeR2R."
  }

  if ($CompositeR2R -and -not $ReadyToRun) {
    throw "-CompositeR2R requires -ReadyToRun."
  }

  $selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
  $singleFileValue = if ($SingleFile) { 'true' } else { 'false' }

  $arguments = @(
    'publish',
    $info.CsprojPath,
    '-c', $Configuration,
    '--disable-build-servers',
    '-p:DebugType=none',
    '--self-contained',
    $selfContainedValue,
    ('-p:PublishSingleFile={0}' -f $singleFileValue),
    '-p:UseAppHost=true'
  )

  if ($RuntimeIdentifier) { $arguments += @('-r', $RuntimeIdentifier) }
  if ($ReadyToRun) { $arguments += '-p:PublishReadyToRun=true' }
  if ($CompositeR2R) { $arguments += '-p:PublishReadyToRunComposite=true' }

  Write-Host "🛠  Publishing $($info.ProjectName) ..."
  & dotnet @arguments | Out-Host
  if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for $($info.ProjectName)."
  }

  $publishDir = Get-AbsolutePath (Compute-PublishDir $info $Configuration $RuntimeIdentifier)
  if (-not (Test-Path -LiteralPath $publishDir)) {
    throw "Publish directory not found: $publishDir"
  }

  return $publishDir
}

function Compute-ContractDir($info, [string] $configuration) {
  if ([string]::IsNullOrWhiteSpace($info.TargetFramework)) {
    throw "TargetFramework not specified in project $($info.ProjectName).csproj"
  }

  return Join-Path $info.ProjectDir "obj\$configuration\$($info.TargetFramework)\plugin-contract"
}

function Resolve-ContractDir($info, [string] $configuration, [string] $rid) {
  $candidateDirs = [System.Collections.Generic.List[string]]::new()
  $candidateDirs.Add((Get-AbsolutePath (Compute-ContractDir $info $configuration)))

  if (-not [string]::IsNullOrWhiteSpace($rid)) {
    $candidateDirs.Add((Get-AbsolutePath (Join-Path $info.ProjectDir "obj\$configuration\$($info.TargetFramework)\$rid\plugin-contract")))
  }

  $candidateDirs.Add((Get-AbsolutePath $info.ProjectDir))

  foreach ($candidateDir in $candidateDirs | Select-Object -Unique) {
    $manifestPath = Join-Path $candidateDir 'manifest.json'
    if (Test-Path -LiteralPath $manifestPath) {
      return $candidateDir
    }
  }

  $globRoot = Join-Path $info.ProjectDir "obj\$configuration\$($info.TargetFramework)"
  if (Test-Path -LiteralPath $globRoot) {
    $discoveredDir = Get-ChildItem -Path $globRoot -Directory -Recurse |
      Where-Object { $_.Name -eq 'plugin-contract' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json')) } |
      Select-Object -First 1

    if ($null -ne $discoveredDir) {
      return $discoveredDir.FullName
    }
  }

  throw "Generated contract directory not found for $($info.ProjectName). Checked: $($candidateDirs -join ', ')"
}

function Convert-ToCanonicalJsonValue($value) {
  if ($null -eq $value) {
    return $null
  }

  if ($value -is [string] -or
      $value -is [bool] -or
      $value -is [byte] -or
      $value -is [int16] -or
      $value -is [int32] -or
      $value -is [int64] -or
      $value -is [uint16] -or
      $value -is [uint32] -or
      $value -is [uint64] -or
      $value -is [decimal] -or
      $value -is [double] -or
      $value -is [single]) {
    return $value
  }

  if ($value -is [System.Collections.IDictionary]) {
    $ordered = [ordered]@{}
    foreach ($key in ($value.Keys | Sort-Object)) {
      $ordered[$key] = Convert-ToCanonicalJsonValue $value[$key]
    }
    return $ordered
  }

  if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) {
    if ($value.PSObject -and $value.PSObject.Properties.Count -gt 0 -and $value -isnot [System.Array]) {
      $ordered = [ordered]@{}
      foreach ($property in ($value.PSObject.Properties | Sort-Object Name)) {
        $ordered[$property.Name] = Convert-ToCanonicalJsonValue $property.Value
      }
      return $ordered
    }

    $items = @()
    foreach ($item in $value) {
      $items += ,(Convert-ToCanonicalJsonValue $item)
    }
    return $items
  }

  if ($value.PSObject -and $value.PSObject.Properties.Count -gt 0) {
    $ordered = [ordered]@{}
    foreach ($property in ($value.PSObject.Properties | Sort-Object Name)) {
      $ordered[$property.Name] = Convert-ToCanonicalJsonValue $property.Value
    }
    return $ordered
  }

  return $value
}

function Get-CanonicalJsonValueFromFile([string] $path) {
  $json = Get-Content -LiteralPath $path -Raw -Encoding UTF8
  return Convert-JsonTextToMinifiedString $json
}

function Get-ContractFingerprintFromRoot([string] $rootPath) {
  $manifestPath = Join-Path $rootPath 'manifest.json'
  if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "manifest.json was not found under $rootPath."
  }

  $contract = [ordered]@{
    manifest = Get-CanonicalJsonValueFromFile $manifestPath
  }

  $schemaSource = Resolve-ContractSchemaSource $rootPath
  if ($schemaSource.Kind -eq 'json') {
    $contract.schemas = Get-CanonicalJsonValueFromFile $schemaSource.Path
  }
  else {
    $schemaMap = [ordered]@{}
    foreach ($schemaFile in $schemaSource.Files | Sort-Object FullName) {
      $relativePath = [System.IO.Path]::GetRelativePath($schemaSource.Path, $schemaFile.FullName).Replace('\', '/')
      $schemaMap[$relativePath] = Get-CanonicalJsonValueFromFile $schemaFile.FullName
    }

    $contract.schemas = $schemaMap
  }

  return (ConvertTo-Json $contract -Depth 100 -Compress)
}

function Get-LatestCanonicalBundle([string] $projectName, [string] $searchRoot) {
  if (-not (Test-Path -LiteralPath $searchRoot)) {
    return $null
  }

  $bundlePattern = "^{0}_(?<version>\d+(\.\d+){{0,3}})\.zip$" -f [regex]::Escape($projectName)
  $matches = foreach ($bundleFile in Get-ChildItem -Path $searchRoot -Recurse -Filter *.zip -File) {
    $match = [regex]::Match($bundleFile.Name, $bundlePattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
      continue
    }

    $bundleVersion = $null
    if (-not [Version]::TryParse($match.Groups['version'].Value, [ref] $bundleVersion)) {
      continue
    }

    [pscustomobject]@{
      Path = $bundleFile.FullName
      Version = [Version]::new($bundleVersion.Major, $bundleVersion.Minor, (Get-VersionPatch $bundleVersion))
    }
  }

  return $matches |
    Sort-Object @{ Expression = { $_.Version.Major }; Descending = $true },
                @{ Expression = { $_.Version.Minor }; Descending = $true },
                @{ Expression = { Get-VersionPatch $_.Version }; Descending = $true } |
    Select-Object -First 1
}

function Get-TempDirectory([string] $prefix) {
  $path = Join-Path ([System.IO.Path]::GetTempPath()) ("{0}_{1}" -f $prefix, [Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $path -Force | Out-Null
  return $path
}

function Get-DirectorySizeBytes([string] $path) {
  if (-not (Test-Path -LiteralPath $path)) {
    return 0
  }

  $sum = (Get-ChildItem -LiteralPath $path -Recurse -File -Force | Measure-Object Length -Sum).Sum
  if ($null -eq $sum) {
    return 0
  }

  return [double] $sum
}

function Format-SizeMiB([double] $bytes) {
  return [math]::Round($bytes / 1MB, 2)
}

function Get-LargestFilesSummary([string] $path, [int] $count = 5) {
  if (-not (Test-Path -LiteralPath $path)) {
    return "<missing>"
  }

  $largestFiles = @(Get-ChildItem -LiteralPath $path -Recurse -File -Force |
    Sort-Object Length -Descending |
    Select-Object -First $count)

  if ($largestFiles.Count -eq 0) {
    return "<none>"
  }

  return (($largestFiles | ForEach-Object {
    $relativePath = [System.IO.Path]::GetRelativePath($path, $_.FullName).Replace('\', '/')
    "{0} ({1} MiB)" -f $relativePath, (Format-SizeMiB $_.Length)
  }) -join ', ')
}

function Write-BundleSizeReport([string] $bundlePath, [string] $appPath) {
  $bundleSizeMiB = Format-SizeMiB (Get-Item -LiteralPath $bundlePath).Length
  $appSizeMiB = Format-SizeMiB (Get-DirectorySizeBytes $appPath)
  $largestFiles = Get-LargestFilesSummary $appPath

  Write-Host ("📦 Bundle size: {0} MiB compressed; app payload: {1} MiB; largest app files: {2}" -f $bundleSizeMiB, $appSizeMiB, $largestFiles)

  if ($MaxBundleSizeMiB -le 0) {
    return
  }

  if (-not $enforceBundleSizeLimit) {
    Write-Warning ("Bundle size limit {0} MiB skipped for explicit self-contained/single-file build. Pass -MaxBundleSizeMiB to enforce a custom limit." -f $MaxBundleSizeMiB)
    return
  }

  if ($bundleSizeMiB -gt $MaxBundleSizeMiB) {
    throw ("Bundle {0} is {1} MiB, above the configured limit of {2} MiB. This usually means a plugin was published self-contained/single-file or picked up an unexpectedly heavy dependency." -f $bundlePath, $bundleSizeMiB, $MaxBundleSizeMiB)
  }
}

function Get-NextBundleVersion($info, [string] $contractRoot, [string] $searchRoot) {
  $declaredVersion = Get-VersionOrThrow $info.Version $info.ProjectName
  $latestBundle = Get-LatestCanonicalBundle $info.ProjectName $searchRoot

  if ($null -eq $latestBundle) {
    return $declaredVersion
  }

  if ($declaredVersion.Major -gt $latestBundle.Version.Major) {
    return $declaredVersion
  }

  if ($declaredVersion.Major -lt $latestBundle.Version.Major) {
    throw "Declared major version $($declaredVersion.ToString(3)) for $($info.ProjectName) is behind latest canonical bundle version $($latestBundle.Version.ToString(3))."
  }

  $currentContractFingerprint = Get-ContractFingerprintFromRoot $contractRoot
  $extractedBundleRoot = Get-TempDirectory "plugin_bundle_compare"

  try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($latestBundle.Path, $extractedBundleRoot)
    $latestContractFingerprint = Get-ContractFingerprintFromRoot $extractedBundleRoot
  }
  finally {
    if (Test-Path -LiteralPath $extractedBundleRoot) {
      Remove-Item -LiteralPath $extractedBundleRoot -Recurse -Force
    }
  }

  if ($currentContractFingerprint -ne $latestContractFingerprint) {
    return [Version]::new($latestBundle.Version.Major, $latestBundle.Version.Minor + 1, 0)
  }

  return [Version]::new($latestBundle.Version.Major, $latestBundle.Version.Minor, (Get-VersionPatch $latestBundle.Version) + 1)
}

function Minify-JsonFile([string] $sourcePath, [string] $destinationPath) {
  $json = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8
  $minified = Convert-JsonTextToMinifiedString $json
  Set-Content -LiteralPath $destinationPath -Value $minified -NoNewline -Encoding UTF8
}

function Convert-JsonTextToMinifiedString([string] $json) {
  $document = [System.Text.Json.JsonDocument]::Parse($json)
  try {
    $stream = [System.IO.MemoryStream]::new()
    $writerOptions = [System.Text.Json.JsonWriterOptions]::new()
    $writerOptions.Indented = $false
    $writer = [System.Text.Json.Utf8JsonWriter]::new($stream, $writerOptions)
    try {
      $document.RootElement.WriteTo($writer)
      $writer.Flush()
      return [System.Text.Encoding]::UTF8.GetString($stream.ToArray())
    }
    finally {
      $writer.Dispose()
      $stream.Dispose()
    }
  }
  finally {
    $document.Dispose()
  }
}

function Copy-FilePreservingOptionalMinify([string] $sourcePath, [string] $destinationPath) {
  if ($Minify) {
    Minify-JsonFile $sourcePath $destinationPath
    return
  }

  Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
}

function Resolve-ContractSchemaSource([string] $primaryRootPath) {
  $candidateRoots = @($primaryRootPath)

  foreach ($candidateRoot in $candidateRoots) {
    $schemasJsonPath = Join-Path $candidateRoot 'schemas.json'
    if (Test-Path -LiteralPath $schemasJsonPath) {
      return [pscustomobject]@{
        Kind = 'json'
        Path = $schemasJsonPath
      }
    }

    $schemasDirPath = Join-Path $candidateRoot 'schemas'
    if (-not (Test-Path -LiteralPath $schemasDirPath)) {
      continue
    }

    $schemaFiles = @(Get-ChildItem -Path $schemasDirPath -Recurse -Filter *.json -File)
    if ($schemaFiles.Count -gt 0) {
      return [pscustomobject]@{
        Kind = 'dir'
        Path = $schemasDirPath
        Files = $schemaFiles
      }
    }
  }

  throw "Neither schemas.json nor a non-empty schemas/ directory was found under $primaryRootPath."
}

function Copy-GeneratedContractToStage([string] $contractRoot, [string] $stageRoot) {
  $manifestPath = Join-Path $contractRoot 'manifest.json'
  if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "manifest.json was not found in generated contract output for $contractRoot."
  }

  Copy-FilePreservingOptionalMinify $manifestPath (Join-Path $stageRoot 'manifest.json')

  $schemaSource = Resolve-ContractSchemaSource $contractRoot
  if ($schemaSource.Kind -eq 'json') {
    Copy-FilePreservingOptionalMinify $schemaSource.Path (Join-Path $stageRoot 'schemas.json')
    return
  }

  $stageSchemasDir = Join-Path $stageRoot 'schemas'
  New-Item -ItemType Directory -Path $stageSchemasDir -Force | Out-Null

  foreach ($schemaFile in $schemaSource.Files) {
    $relativePath = [System.IO.Path]::GetRelativePath($schemaSource.Path, $schemaFile.FullName)
    $destinationPath = Join-Path $stageSchemasDir $relativePath
    $destinationDirectory = Split-Path -Parent $destinationPath
    if ($destinationDirectory) {
      New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }

    Copy-FilePreservingOptionalMinify $schemaFile.FullName $destinationPath
  }
}

function Read-ManifestAbiOrThrow([string] $stageRoot, [string] $projectName) {
  $manifestPath = Join-Path $stageRoot 'manifest.json'
  if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Generated manifest.json was not staged for $projectName."
  }

  $manifest = Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
  if ($null -eq $manifest.abi) {
    throw "Generated manifest.json for $projectName is missing required abi metadata."
  }

  return $manifest.abi
}

function Bundle-OneProject([string] $csprojPath, [string] $resolvedOutputDir) {
  $info = Get-CsprojInfo $csprojPath
  if (-not (Is-ExecutableProject $info)) {
    Write-Host "⏭️  Skipping non-executable project: $($info.ProjectName)"
    return
  }

  $publishDir = Invoke-PluginPublish $info
  $contractDir = Resolve-ContractDir $info $Configuration $RuntimeIdentifier

  $resolvedVersion = Get-NextBundleVersion $info $contractDir $resolvedOutputDir
  $declaredVersion = Get-VersionOrThrow $info.Version $info.ProjectName

  if ((Compare-VersionCore $resolvedVersion $declaredVersion) -ne 0) {
    Write-Host "🔢 Updating $($info.ProjectName) version from $($declaredVersion.ToString(3)) to $($resolvedVersion.ToString(3)) ..."
    Set-ProjectVersion $csprojPath $resolvedVersion
    $info = Get-CsprojInfo $csprojPath
    $publishDir = Invoke-PluginPublish $info
    $contractDir = Resolve-ContractDir $info $Configuration $RuntimeIdentifier
  }

  $stageRoot = Get-TempDirectory "plugin_stage"
  $stageApp = Join-Path $stageRoot 'app'

  try {
    New-Item -ItemType Directory -Path $stageApp -Force | Out-Null
    Get-ChildItem -Path $publishDir -Force | Copy-Item -Destination $stageApp -Recurse -Force -ErrorAction Stop
    Copy-GeneratedContractToStage $contractDir $stageRoot
    $pluginAbi = Read-ManifestAbiOrThrow $stageRoot $info.ProjectName

    $bundleMeta = [ordered]@{
      name = $info.ProjectName
      version = $resolvedVersion.ToString(3)
      abi = $pluginAbi
      createdUtc = (Get-Date).ToUniversalTime().ToString('o')
      publishDir = $publishDir
      rid = if ($RuntimeIdentifier) { $RuntimeIdentifier } else { $null }
      selfContained = [bool] $SelfContained
      singleFile = [bool] $SingleFile
      useAppHost = $true
      configuration = $Configuration
      maxBundleSizeMiB = if ($MaxBundleSizeMiB -gt 0) { $MaxBundleSizeMiB } else { $null }
      files = [ordered]@{
        manifest = 'manifest.json'
        schemas = if (Test-Path -LiteralPath (Join-Path $stageRoot 'schemas.json')) { 'schemas.json' } else { 'schemas/' }
        appDir = 'app/'
      }
    }
    ($bundleMeta | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath (Join-Path $stageRoot 'bundle.meta.json') -NoNewline -Encoding UTF8

    New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null
    $bundlePath = Join-Path $resolvedOutputDir ("{0}_{1}.zip" -f $info.ProjectName, $resolvedVersion.ToString(3))

    if ((Test-Path -LiteralPath $bundlePath) -and (-not $Overwrite)) {
      throw "Output already exists: $bundlePath (use -Overwrite to replace)."
    }

    if (Test-Path -LiteralPath $bundlePath) {
      Remove-Item -LiteralPath $bundlePath -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $bundlePath)
    Write-BundleSizeReport $bundlePath $stageApp
    Write-Host "✅ Bundled: $bundlePath"
  }
  finally {
    if (Test-Path -LiteralPath $stageRoot) {
      Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
  }
}

$resolvedRootDir = Get-AbsolutePath $RootDir
$resolvedOutputDir = if ($OutputDir) {
  Get-AbsolutePath $OutputDir
}
else {
  Join-Path $resolvedRootDir '_bundles'
}

$pattern = if ($ProjectFilter) { $ProjectFilter } else { '*.csproj' }
$projects = Get-ChildItem -LiteralPath $resolvedRootDir -Recurse -Filter $pattern -File | Select-Object -ExpandProperty FullName

if (-not $projects -or $projects.Count -eq 0) {
  Write-Warning "No projects found under $resolvedRootDir (pattern: $pattern)."
  return
}

foreach ($csprojPath in $projects) {
  try {
    Bundle-OneProject $csprojPath $resolvedOutputDir
  }
  catch {
    Write-Warning "⚠️  Failed for project: $csprojPath`n$($_.Exception.Message)"
  }
}

Write-Host "Done."

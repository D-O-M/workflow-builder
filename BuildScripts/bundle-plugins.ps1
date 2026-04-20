<#
.SYNOPSIS
  Publishes and bundles executable plugin projects into canonical versioned ZIPs.

.DESCRIPTION
  For each executable SDK-style plugin project under -RootDir, this script:
    1) Publishes the project
    2) Reads the generated contract artifacts from publish output:
         manifest.json + (schemas.json OR schemas\*.json)
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
  Switch: publish self-contained (requires -RuntimeIdentifier). Default: false

.PARAMETER SingleFile
  Switch: publish as single file. Default: false

.PARAMETER ReadyToRun
  Switch: publish ReadyToRun. Default: false

.PARAMETER CompositeR2R
  Switch: composite ReadyToRun (requires ReadyToRun). Default: false

.PARAMETER Minify
  Switch: minify manifest/schemas JSON in the bundle. Default: false

.PARAMETER Overwrite
  Switch: overwrite an already-existing canonical bundle for the final resolved version.
  Default: false

.PARAMETER ProjectFilter
  Optional glob to filter project file names (for example "*Plugin*.csproj").

.EXAMPLE
  .\bundle-plugins.ps1 -RootDir .\Backend.Plugins -OutputDir .\Backend.Plugins\_bundles `
    -Configuration Release -RuntimeIdentifier win-x64 -SingleFile -Overwrite
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
  [string] $ProjectFilter
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

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

  $writer = [System.Xml.XmlWriter]::Create($path, $settings)
  try {
    $xml.Save($writer)
  }
  finally {
    $writer.Dispose()
  }
}

function Set-ProjectVersion([string] $csprojPath, [Version] $version) {
  [xml]$xml = Get-Content -LiteralPath $csprojPath -Raw -Encoding UTF8
  $projectNode = $xml.Project
  if ($null -eq $projectNode) {
    throw "Could not load project XML for $csprojPath."
  }

  $propertyGroup = $projectNode.PropertyGroup | Select-Object -First 1
  if ($null -eq $propertyGroup) {
    $propertyGroup = $xml.CreateElement("PropertyGroup")
    $projectNode.AppendChild($propertyGroup) | Out-Null
  }

  $versionNode = $propertyGroup.Version | Select-Object -First 1
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

  $arguments = @(
    'publish',
    $info.CsprojPath,
    '-c', $Configuration,
    '-p:DebugType=none'
  )

  if ($RuntimeIdentifier) { $arguments += @('-r', $RuntimeIdentifier) }
  if ($SelfContained) { $arguments += @('--self-contained', 'true') }
  if ($SingleFile) { $arguments += '-p:PublishSingleFile=true' }
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
  $json = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
  return Convert-ToCanonicalJsonValue $json
}

function Get-ContractFingerprintFromRoot([string] $rootPath, [string] $fallbackRootPath = $null) {
  $manifestPath = Join-Path $rootPath 'manifest.json'
  if (-not (Test-Path -LiteralPath $manifestPath)) {
    if ($fallbackRootPath) {
      $manifestPath = Join-Path $fallbackRootPath 'manifest.json'
    }
  }
  if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "manifest.json was not found under $rootPath or fallback root $fallbackRootPath."
  }

  $contract = [ordered]@{
    manifest = Get-CanonicalJsonValueFromFile $manifestPath
  }

  $schemaSource = Resolve-ContractSchemaSource $rootPath $fallbackRootPath
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

function Get-NextBundleVersion($info, [string] $publishDir, [string] $searchRoot) {
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

  $currentContractFingerprint = Get-ContractFingerprintFromRoot $publishDir $info.ProjectDir
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
  $json = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
  $minified = ConvertTo-Json (Convert-ToCanonicalJsonValue $json) -Depth 100 -Compress
  Set-Content -LiteralPath $destinationPath -Value $minified -NoNewline -Encoding UTF8
}

function Copy-FilePreservingOptionalMinify([string] $sourcePath, [string] $destinationPath) {
  if ($Minify) {
    Minify-JsonFile $sourcePath $destinationPath
    return
  }

  Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
}

function Resolve-ContractSchemaSource([string] $primaryRootPath, [string] $fallbackRootPath = $null) {
  $candidateRoots = @($primaryRootPath)
  if ($fallbackRootPath) {
    $candidateRoots += $fallbackRootPath
  }

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

  throw "Neither schemas.json nor a non-empty schemas/ directory was found under $primaryRootPath or fallback root $fallbackRootPath."
}

function Copy-PublishContractToStage([string] $publishDir, [string] $fallbackRootPath, [string] $stageRoot) {
  $manifestPath = Join-Path $publishDir 'manifest.json'
  if (-not (Test-Path -LiteralPath $manifestPath)) {
    if ($fallbackRootPath) {
      $manifestPath = Join-Path $fallbackRootPath 'manifest.json'
    }
  }
  if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "manifest.json was not found in publish output or fallback root for $publishDir."
  }

  Copy-FilePreservingOptionalMinify $manifestPath (Join-Path $stageRoot 'manifest.json')

  $schemaSource = Resolve-ContractSchemaSource $publishDir $fallbackRootPath
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

function Bundle-OneProject([string] $csprojPath, [string] $resolvedOutputDir) {
  $info = Get-CsprojInfo $csprojPath
  if (-not (Is-ExecutableProject $info)) {
    Write-Host "⏭️  Skipping non-executable project: $($info.ProjectName)"
    return
  }

  $publishDir = Invoke-PluginPublish $info
  $resolvedVersion = Get-NextBundleVersion $info $publishDir $resolvedOutputDir
  $declaredVersion = Get-VersionOrThrow $info.Version $info.ProjectName

  if ((Compare-VersionCore $resolvedVersion $declaredVersion) -ne 0) {
    Write-Host "🔢 Updating $($info.ProjectName) version from $($declaredVersion.ToString(3)) to $($resolvedVersion.ToString(3)) ..."
    Set-ProjectVersion $csprojPath $resolvedVersion
    $info = Get-CsprojInfo $csprojPath
    $publishDir = Invoke-PluginPublish $info
  }

  $stageRoot = Get-TempDirectory "plugin_stage"
  $stageApp = Join-Path $stageRoot 'app'

  try {
    New-Item -ItemType Directory -Path $stageApp -Force | Out-Null
    Get-ChildItem -Path $publishDir -Force | Copy-Item -Destination $stageApp -Recurse -Force -ErrorAction Stop
    Copy-PublishContractToStage $publishDir $info.ProjectDir $stageRoot

    $bundleMeta = [ordered]@{
      name = $info.ProjectName
      version = $resolvedVersion.ToString(3)
      createdUtc = (Get-Date).ToUniversalTime().ToString('o')
      publishDir = $publishDir
      rid = if ($RuntimeIdentifier) { $RuntimeIdentifier } else { $null }
      selfContained = [bool] $SelfContained
      configuration = $Configuration
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

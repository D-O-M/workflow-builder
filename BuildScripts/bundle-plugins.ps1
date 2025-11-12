<#
.SYNOPSIS
  Publishes and bundles ALL executable plugin projects under a given root folder.

.DESCRIPTION
  For each SDK-style .csproj under -RootDir that looks like an APP (not a class library),
  this script:
    1) Runs `dotnet publish` with your chosen settings
    2) Gathers manifest.json + schemas (schemas.json preferred, or schemas\*.json)
    3) Creates a readable, portable bundle ZIP:
         <OutputDir>\<ProjectName>.plugin.zip
         ├─ manifest.json
         ├─ schemas.json  OR  schemas\*.json
         └─ app\  (contents of dotnet publish)

  "Executable plugin" detection heuristics (any true ⇒ treated as plugin):
    - <OutputType>Exe</OutputType> OR <OutputType>WinExe</OutputType> in the csproj
    - OR presence of Program.cs (top-level statements/Program entry-point)

.PARAMETER RootDir
  Folder to search for .csproj files (recursively).

.PARAMETER OutputDir
  Where *.plugin.zip files are written. Defaults to "<RootDir>\_bundles".

.PARAMETER Configuration
  Build/publish configuration. Default: Release

.PARAMETER RuntimeIdentifier
  RID when publishing (e.g., win-x64, linux-x64, osx-arm64). If omitted, framework-dependent publish.

.PARAMETER SelfContained
  Switch: publish self-contained (requires -RuntimeIdentifier). Default: false

.PARAMETER SingleFile
  Switch: publish as single file. Default: false

.PARAMETER ReadyToRun
  Switch: publish ReadyToRun. Default: false

.PARAMETER CompositeR2R
  Switch: composite ReadyToRun (only relevant if ReadyToRun is true). Default: false

.PARAMETER Minify
  Switch: minify manifest/schemas JSON in bundle. Default: false

.PARAMETER Overwrite
  Switch: overwrite existing bundle zip. Default: false

.PARAMETER ProjectFilter
  Optional glob to filter project file names (e.g., "*Plugin*.csproj")

.EXAMPLE
  .\bundle-plugins.ps1 -RootDir .\Backend.Plugins -OutputDir .\Backend.Plugins\_bundles `
    -Configuration Release -RuntimeIdentifier linux-x64 -SelfContained -SingleFile `
    -ReadyToRun -CompositeR2R -Minify -Overwrite
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

# ---------------- helpers ----------------

function Resolve-FullPath([string] $path) {
  (Resolve-Path -LiteralPath $path).Path
}

function Get-CsprojInfo([string] $csprojPath) {
  # Returns: @{ ProjectDir; ProjectName; TargetFramework; OutputType; HasProgramCs }
  $raw = Get-Content -LiteralPath $csprojPath -Raw -Encoding UTF8
  [xml]$xml = $raw

  function Get-NodeText($doc, [string]$localName) {
    try {
      $n = $doc.SelectSingleNode("//*[local-name()='$localName']")
      if ($null -ne $n) { return ($n.InnerText.Trim()) }
    } catch { }
    return $null
  }

  $projDir  = Split-Path -Parent $csprojPath
  $projName = [System.IO.Path]::GetFileNameWithoutExtension($csprojPath)

  # TargetFramework or TargetFrameworks (take the first if multiple)
  $tfm  = Get-NodeText $xml 'TargetFramework'
  $tfms = Get-NodeText $xml 'TargetFrameworks'
  if ([string]::IsNullOrWhiteSpace($tfm) -and -not [string]::IsNullOrWhiteSpace($tfms)) {
    $tfm = ($tfms -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[0]
  }

  # OutputType (Exe/WinExe/Library). May be empty if not set explicitly.
  $outputType = Get-NodeText $xml 'OutputType'
  if ($null -eq $outputType) { $outputType = '' }

  # Program.cs detection (simple heuristic)
  $hasProgram = Test-Path -LiteralPath (Join-Path $projDir 'Program.cs')

  return @{
    ProjectDir      = $projDir
    ProjectName     = $projName
    TargetFramework = $tfm
    OutputType      = $outputType
    HasProgramCs    = $hasProgram
  }
}

function Is-ExecutableProject($info) {
  $ot = ''
  if ($null -ne $info.OutputType) { $ot = $info.OutputType.ToString() }
  $ot = $ot.ToLowerInvariant()

  if ($ot -eq 'exe' -or $ot -eq 'winexe') { return $true }
  if ($info.HasProgramCs) { return $true }
  return $false
}

function Compute-PublishDir($info, [string] $config, [string] $rid) {
  if ([string]::IsNullOrWhiteSpace($info.TargetFramework)) {
    throw "TargetFramework not specified in project $($info.ProjectName).csproj"
  }
  if ([string]::IsNullOrWhiteSpace($rid)) {
    # framework-dependent
    return Join-Path $info.ProjectDir "bin\$config\$($info.TargetFramework)\publish\"
  } else {
    # RID-specific
    return Join-Path $info.ProjectDir "bin\$config\$($info.TargetFramework)\$rid\publish\"
  }
}

function Prefer-Path([string[]] $candidates) {
  foreach ($c in $candidates) {
    if ($c -and (Test-Path -LiteralPath $c)) { return (Resolve-FullPath $c) }
  }
  return $null
}

function Minify-JsonFile([string] $src, [string] $dest) {
  $json = Get-Content -Raw -LiteralPath $src | ConvertFrom-Json
  $min = $json | ConvertTo-Json -Depth 100 -Compress
  Set-Content -LiteralPath $dest -Value $min -NoNewline
}

function Copy-Schemas([string] $schemasJson, [string] $schemasDir, [string] $stageRoot, [switch] $Minify) {
  $outJson  = Join-Path $stageRoot "schemas.json"
  $outDir   = Join-Path $stageRoot "schemas"

  if ($schemasJson) {
    if ($Minify) { Minify-JsonFile $schemasJson $outJson } else { Copy-Item -Path $schemasJson -Destination $outJson -Force }
  }
  elseif ($schemasDir) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    if ($Minify) {
      Get-ChildItem -Path $schemasDir -Recurse -Filter *.json | ForEach-Object {
        $rel  = $_.FullName.Substring($schemasDir.Length).TrimStart('\','/')
        $dest = Join-Path $outDir $rel
        New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($dest)) | Out-Null
        Minify-JsonFile $_.FullName $dest
      }
    } else {
      Copy-Item -Path $schemasDir -Destination $outDir -Recurse -Force
    }
  }
  else {
    throw "No schemas provided."
  }
}

function Bundle-OneProject([string] $csprojPath) {
  $info = Get-CsprojInfo $csprojPath
  if (-not (Is-ExecutableProject $info)) {
    Write-Host "⏭️  Skipping non-executable project: $($info.ProjectName)"
    return
  }

  # Publish args
  $args = @("publish", "`"$csprojPath`"", "-c", $Configuration)
  if ($RuntimeIdentifier) { $args += @("-r", $RuntimeIdentifier) }
  if ($SelfContained)     { $args += @("--self-contained", "true") }
  if ($SingleFile)        { $args += @("-p:PublishSingleFile=true") }
  if ($ReadyToRun)        { $args += @("-p:PublishReadyToRun=true") }
  if ($CompositeR2R)      { $args += @("-p:PublishReadyToRunComposite=true") }
  # Reasonable default: drop pdb/xml on publish
  $args += @("-p:DebugType=none")

  Write-Host "🛠  Publishing $($info.ProjectName) ..."
  $psi = @{
    FilePath = "dotnet"
    ArgumentList = $args
    WorkingDirectory = $info.ProjectDir
    NoNewWindow = $true
    Wait = $true
  }
  $p = Start-Process @psi -PassThru
  if ($p.ExitCode -ne 0) { throw "dotnet publish failed for $($info.ProjectName)" }

  $publishDir = Resolve-FullPath (Compute-PublishDir $info $Configuration $RuntimeIdentifier)
  if (-not (Test-Path -LiteralPath $publishDir)) { throw "Publish directory not found: $publishDir" }

  # Stage layout
  $stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("plugin_stage_" + [Guid]::NewGuid().ToString("N"))
  $stageApp  = Join-Path $stageRoot "app"
  New-Item -ItemType Directory -Force -Path $stageApp | Out-Null

  # Copy app files (all publish output) - use -Path (supports wildcards)
  Get-ChildItem -Path $publishDir -Force | Copy-Item -Destination $stageApp -Recurse -Force -ErrorAction Stop

  # Find manifest/schemas: prefer publish dir, fall back to project dir
  $manifest = Prefer-Path @(
    (Join-Path $publishDir "manifest.json"),
    (Join-Path $info.ProjectDir "manifest.json")
  )
  if (-not $manifest) { throw "manifest.json not found for $($info.ProjectName). Ensure it exists in publish or project root." }

  $schemasJson = Prefer-Path @(
    (Join-Path $publishDir "schemas.json"),
    (Join-Path $info.ProjectDir "schemas.json")
  )
  $schemasDir  = $null
  if (-not $schemasJson) {
    $schemasDir = Prefer-Path @(
      (Join-Path $publishDir "schemas"),
      (Join-Path $info.ProjectDir "schemas")
    )
    if (-not $schemasDir) { throw "Schemas not found for $($info.ProjectName). Provide schemas.json or schemas/." }
  }

  # Write manifest to stage
  $stageManifest = Join-Path $stageRoot "manifest.json"
  if ($Minify) { Minify-JsonFile $manifest $stageManifest }
  else         { Copy-Item -Path $manifest -Destination $stageManifest -Force }

  # Write schemas to stage
  Copy-Schemas -schemasJson $schemasJson -schemasDir $schemasDir -stageRoot $stageRoot -Minify:$Minify

  # bundle.meta.json (for traceability)
  $schemasValue = "schemas/"
  if (Test-Path -LiteralPath (Join-Path $stageRoot "schemas.json")) { $schemasValue = "schemas.json" }

  $meta = @{
    name          = $info.ProjectName
    createdUtc    = (Get-Date).ToUniversalTime().ToString("o")
    publishDir    = $publishDir
    hasExe        = ((Get-ChildItem -LiteralPath $stageApp -Filter *.exe -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0)
    rid           = $RuntimeIdentifier
    selfContained = [bool]$SelfContained
    configuration = $Configuration
    files = @{
      manifest = "manifest.json"
      schemas  = $schemasValue
      appDir   = "app/"
    }
  }
  $meta | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stageRoot "bundle.meta.json") -NoNewline

  # Zip
  if (-not $OutputDir) { $OutputDir = Join-Path $RootDir "_bundles" }
  New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
  $outZip = Join-Path $OutputDir ("{0}.plugin.zip" -f $info.ProjectName)

  if ((Test-Path -LiteralPath $outZip) -and (-not $Overwrite)) {
    throw "Output already exists: $outZip (use -Overwrite to replace)"
  }
  if (Test-Path -LiteralPath $outZip) { Remove-Item -LiteralPath $outZip -Force }

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $outZip)

  Write-Host "✅ Bundled: $outZip"

  # Cleanup
  Remove-Item -LiteralPath $stageRoot -Recurse -Force
}

# ---------------- main ----------------

$RootDir = Resolve-FullPath $RootDir
if ($OutputDir) { $OutputDir = Resolve-FullPath $OutputDir }

$pattern = if ($ProjectFilter) { $ProjectFilter } else { "*.csproj" }
$projects = Get-ChildItem -LiteralPath $RootDir -Recurse -Filter $pattern -File | Select-Object -ExpandProperty FullName

if (-not $projects -or $projects.Count -eq 0) {
  Write-Warning "No projects found under $RootDir (pattern: $pattern)."
  return
}

foreach ($csproj in $projects) {
  try {
    Bundle-OneProject $csproj
  }
  catch {
    Write-Warning "⚠️  Failed for project: $csproj`n$($_.Exception.Message)"
  }
}

Write-Host "Done."

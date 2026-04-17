[CmdletBinding()]
param(
  [string] $OutputDir = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$integrationRoot = Resolve-Path (Join-Path $scriptDir "..")
$repoRoot = Resolve-Path (Join-Path $integrationRoot "..")
$pluginRoot = Resolve-Path (Join-Path $integrationRoot "Plugins")

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
  $OutputDir = Join-Path ([System.IO.Path]::GetTempPath()) "workflow-builder-integration-bundles"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$bundleScript = Join-Path $repoRoot "BuildScripts/bundle-plugins.ps1"

& $bundleScript `
  -RootDir $pluginRoot `
  -OutputDir $OutputDir `
  -Configuration Release `
  -Overwrite

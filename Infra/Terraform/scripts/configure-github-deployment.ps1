param(
    [string] $TerraformDirectory = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $Repository,
    [string] $TargetEnvironment = 'demo',
    [string] $ImageNamespace = 'workflow-builder',
    [string] $ImageTag,
    [string] $AzureClientId = $env:AZURE_CLIENT_ID,
    [string] $AzureTenantId = $env:AZURE_TENANT_ID,
    [string] $AzureSubscriptionId = $env:AZURE_SUBSCRIPTION_ID,
    [switch] $RunWorkflow,
    [switch] $RunDatabaseMigrations,
    [switch] $NoStaticWebApp,
    [switch] $NoPluginBundles,
    [switch] $SkipStaticWebAppToken,
    [switch] $WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-Tool {
    param([string[]] $Names)

    foreach ($name in $Names) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command) {
            return $command.Source
        }
    }

    throw "Missing required tool. Install one of: $($Names -join ', ')"
}

function Invoke-Logged {
    param(
        [string] $FilePath,
        [string[]] $Arguments,
        [string] $SecretBody
    )

    $display = "$FilePath $($Arguments -join ' ')"
    if ($SecretBody) {
        $display = "$display <secret>"
    }

    if ($WhatIf) {
        Write-Host "DRY RUN: $display"
        return
    }

    if ($SecretBody) {
        $SecretBody | & $FilePath @Arguments
    }
    else {
        & $FilePath @Arguments
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $display"
    }
}

function Get-OutputValue {
    param(
        [pscustomobject] $Outputs,
        [string] $Name
    )

    $property = $Outputs.PSObject.Properties[$Name]
    if (-not $property) {
        return $null
    }

    return $property.Value.value
}

function Test-HasValue {
    param([object] $Value)

    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [string]) {
        return -not [string]::IsNullOrWhiteSpace($Value)
    }

    return $true
}

function Get-ObjectPropertyValue {
    param(
        [object] $Object,
        [string] $Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if (-not $property) {
        return $null
    }

    return $property.Value
}

function Assert-HasValue {
    param(
        [string] $Name,
        [object] $Value
    )

    if (-not (Test-HasValue $Value)) {
        throw "Missing Terraform output value '$Name'. Run Terraform/OpenTofu apply or refresh after pulling the latest Terraform outputs."
    }
}

function Set-GitHubVariable {
    param(
        [string] $Name,
        [object] $Value
    )

    if (-not (Test-HasValue $Value)) {
        return
    }

    Write-Host "Setting GitHub variable $Name"
    Invoke-Logged -FilePath $script:GitHub -Arguments @('variable', 'set', $Name, '--repo', $Repository, '--body', [string] $Value)
}

function Set-GitHubSecret {
    param(
        [string] $Name,
        [object] $Value
    )

    if (-not (Test-HasValue $Value)) {
        return
    }

    Write-Host "Setting GitHub secret $Name"
    Invoke-Logged -FilePath $script:GitHub -Arguments @('secret', 'set', $Name, '--repo', $Repository) -SecretBody ([string] $Value)
}

$githubCommand = Get-Command gh -ErrorAction SilentlyContinue
if ($githubCommand) {
    $script:GitHub = $githubCommand.Source
}
elseif ($WhatIf) {
    $script:GitHub = 'gh'
}
else {
    throw 'Missing required tool: gh. Install GitHub CLI or rerun with -WhatIf to preview.'
}

$terraform = Resolve-Tool @('tofu', 'terraform')

if (-not (Test-Path $TerraformDirectory)) {
    throw "Terraform directory does not exist: $TerraformDirectory"
}

if (-not $Repository) {
    $Repository = (& $script:GitHub repo view --json nameWithOwner --jq '.nameWithOwner' 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $Repository) {
        throw 'Could not infer the GitHub repository. Pass -Repository owner/name.'
    }
}

Push-Location $TerraformDirectory
try {
    $outputsJson = & $terraform output -json
    if ($LASTEXITCODE -ne 0) {
        throw "$terraform output -json failed. Run Terraform/OpenTofu apply first."
    }
}
finally {
    Pop-Location
}

$outputs = $outputsJson | ConvertFrom-Json
$containerApps = Get-OutputValue $outputs 'container_app_names'
$functionApps = Get-OutputValue $outputs 'function_app_names'

$resourceGroupName = Get-OutputValue $outputs 'resource_group_name'
$containerRegistryName = Get-OutputValue $outputs 'container_registry_name'
$containerRegistryLoginServer = Get-OutputValue $outputs 'container_registry_login_server'
$apiContainerAppName = Get-ObjectPropertyValue $containerApps 'api'
$signalrContainerAppName = Get-ObjectPropertyValue $containerApps 'signalr'
$engineContainerAppName = Get-ObjectPropertyValue $containerApps 'engine'
$dispatcherContainerAppName = Get-ObjectPropertyValue $containerApps 'dispatcher'
$projectionWorkerContainerAppName = Get-ObjectPropertyValue $containerApps 'projection_worker'
$maintenanceContainerAppName = Get-ObjectPropertyValue $containerApps 'maintenance'
$progressBroadcasterFunctionAppName = Get-ObjectPropertyValue $functionApps 'progress_broadcaster'
$pluginDetectorFunctionAppName = Get-ObjectPropertyValue $functionApps 'plugin_detector'
$storageAccountName = Get-OutputValue $outputs 'storage_account_name'
$pluginsContainerName = Get-OutputValue $outputs 'plugins_container_name'
$appConfigurationEndpoint = Get-OutputValue $outputs 'app_configuration_endpoint'
$keyVaultName = Get-OutputValue $outputs 'key_vault_name'
$staticWebAppToken = Get-OutputValue $outputs 'static_web_app_deployment_token'

Assert-HasValue 'resource_group_name' $resourceGroupName
Assert-HasValue 'container_registry_name' $containerRegistryName
Assert-HasValue 'container_registry_login_server' $containerRegistryLoginServer
Assert-HasValue 'container_app_names.api' $apiContainerAppName
Assert-HasValue 'container_app_names.signalr' $signalrContainerAppName
Assert-HasValue 'container_app_names.engine' $engineContainerAppName
Assert-HasValue 'container_app_names.dispatcher' $dispatcherContainerAppName
Assert-HasValue 'storage_account_name' $storageAccountName
Assert-HasValue 'plugins_container_name' $pluginsContainerName
if ($RunDatabaseMigrations) {
    Assert-HasValue 'key_vault_name' $keyVaultName
}

$deployProjectionWorker = Test-HasValue $projectionWorkerContainerAppName
$deployMaintenanceWorker = Test-HasValue $maintenanceContainerAppName
$deployProgressBroadcaster = Test-HasValue $progressBroadcasterFunctionAppName
$deployPluginDetector = Test-HasValue $pluginDetectorFunctionAppName
$deployStaticWebApp = -not $NoStaticWebApp
$deployPluginBundles = -not $NoPluginBundles

$requiredAzureValues = @{
    AZURE_CLIENT_ID       = $AzureClientId
    AZURE_TENANT_ID       = $AzureTenantId
    AZURE_SUBSCRIPTION_ID = $AzureSubscriptionId
}

$missingAzureValues = @($requiredAzureValues.GetEnumerator() | Where-Object { -not (Test-HasValue $_.Value) } | ForEach-Object { $_.Key })
if ($missingAzureValues.Count -gt 0) {
    throw "Missing Azure OIDC values. Pass parameters or set environment variables: $($missingAzureValues -join ', ')"
}

Set-GitHubVariable 'DEPLOY_ENVIRONMENT_NAME' $TargetEnvironment
Set-GitHubVariable 'AZURE_RESOURCE_GROUP' $resourceGroupName
Set-GitHubVariable 'ACR_NAME' $containerRegistryName
Set-GitHubVariable 'ACR_LOGIN_SERVER' $containerRegistryLoginServer
Set-GitHubVariable 'IMAGE_NAMESPACE' $ImageNamespace
Set-GitHubVariable 'BUILD_IMAGES' 'true'
Set-GitHubVariable 'PUSH_LATEST' 'false'
Set-GitHubVariable 'BUILD_CONFIGURATION' 'Release'
Set-GitHubVariable 'PLATFORMS' 'linux/amd64'
Set-GitHubVariable 'DEPLOY_PROJECTION_WORKER' ([string] $deployProjectionWorker).ToLowerInvariant()
Set-GitHubVariable 'DEPLOY_MAINTENANCE_WORKER' ([string] $deployMaintenanceWorker).ToLowerInvariant()
Set-GitHubVariable 'DEPLOY_PROGRESS_BROADCASTER' ([string] $deployProgressBroadcaster).ToLowerInvariant()
Set-GitHubVariable 'DEPLOY_PLUGIN_DETECTOR' ([string] $deployPluginDetector).ToLowerInvariant()
Set-GitHubVariable 'DEPLOY_STATIC_WEB_APP' ([string] $deployStaticWebApp).ToLowerInvariant()
Set-GitHubVariable 'DEPLOY_PLUGIN_BUNDLES' ([string] $deployPluginBundles).ToLowerInvariant()
Set-GitHubVariable 'RUN_DATABASE_MIGRATIONS' ([string] $RunDatabaseMigrations.IsPresent).ToLowerInvariant()
Set-GitHubVariable 'BACKEND_API_CONTAINER_APP_NAME' $apiContainerAppName
Set-GitHubVariable 'BACKEND_SIGNALR_CONTAINER_APP_NAME' $signalrContainerAppName
Set-GitHubVariable 'BACKEND_ENGINE_CONTAINER_APP_NAME' $engineContainerAppName
Set-GitHubVariable 'BACKEND_DISPATCHER_CONTAINER_APP_NAME' $dispatcherContainerAppName
Set-GitHubVariable 'BACKEND_PROJECTION_WORKER_CONTAINER_APP_NAME' $projectionWorkerContainerAppName
Set-GitHubVariable 'BACKEND_MAINTENANCE_CONTAINER_APP_NAME' $maintenanceContainerAppName
Set-GitHubVariable 'PROGRESS_BROADCASTER_FUNCTION_APP_NAME' $progressBroadcasterFunctionAppName
Set-GitHubVariable 'PLUGIN_DETECTOR_FUNCTION_APP_NAME' $pluginDetectorFunctionAppName
Set-GitHubVariable 'AZURE_STORAGE_ACCOUNT_NAME' $storageAccountName
Set-GitHubVariable 'PLUGINS_CONTAINER_NAME' $pluginsContainerName
Set-GitHubVariable 'AZURE_APP_CONFIGURATION_ENDPOINT' $appConfigurationEndpoint
Set-GitHubVariable 'AZURE_KEY_VAULT_NAME' $keyVaultName
Set-GitHubVariable 'PLUGIN_RUNTIME_IDENTIFIER' 'linux-x64'
Set-GitHubVariable 'AZURE_CLIENT_ID' $AzureClientId
Set-GitHubVariable 'AZURE_TENANT_ID' $AzureTenantId
Set-GitHubVariable 'AZURE_SUBSCRIPTION_ID' $AzureSubscriptionId

if (-not $SkipStaticWebAppToken) {
    Set-GitHubSecret 'AZURE_STATIC_WEB_APPS_API_TOKEN' $staticWebAppToken
}

if ($RunWorkflow) {
    $workflowArgs = @(
        'workflow', 'run', 'deploy-azure-demo.yml',
        '--repo', $Repository,
        '--ref', (& git rev-parse --abbrev-ref HEAD),
        '-f', "target_environment=$TargetEnvironment"
    )

    if ($ImageTag) {
        $workflowArgs += @('-f', "image_tag=$ImageTag")
    }

    Write-Host 'Starting Deploy Azure Demo workflow'
    Invoke-Logged -FilePath $script:GitHub -Arguments $workflowArgs
}

Write-Host "GitHub deployment configuration updated for $Repository."
if (-not $RunWorkflow) {
    Write-Host 'Run the Deploy Azure Demo workflow in GitHub Actions, or rerun this script with -RunWorkflow.'
}

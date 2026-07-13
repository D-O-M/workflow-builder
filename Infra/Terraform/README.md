# WorkflowBuilder Azure Demo Terraform

This Terraform creates a low-cost Azure demo environment for WorkflowBuilder. It provisions infrastructure and service shells only. Application image build, image push, Function App package deployment, and Static Web App content deployment stay in your external deployment route.

## Architecture

Public ingress:

- Azure Static Web Apps frontend
- `backend-api` Azure Container App
- `backend-signalr` Azure Container App
- optional Keycloak Azure Container App

Internal/no public ingress:

- `backend-engine` Azure Container App worker
- `backend-dispatcher` Azure Container App worker
- optional `backend-projection-worker`
- optional `backend-maintenance`
- optional Function App shells for `progress-broadcaster` and `plugin-detector`

Data plane:

- Azure SQL Database for blueprint/task metadata
- Azure Cosmos DB database `enginedb` with containers matching `Backend.Topology`
- Azure Storage containers `plugins` and `artifacts`
- Azure Service Bus queues `processsteps` and `pluginreloadqueue`
- Azure App Configuration Standard for shared app settings
- optional Key Vault for secret storage and App Configuration Key Vault references

Grafana, Loki, Tempo, Prometheus, AKS, VMs, VMSS, Azure Batch, and active ACA Jobs are intentionally not deployed.

## Cheap Demo Profile

The demo defaults keep cost low:

- Container Apps min replicas are `0`
- max replicas default to `1`
- Cosmos free tier is enabled at account creation
- Service Bus defaults to `Basic`
- Storage uses Standard LRS
- Static Web Apps uses the Free SKU
- Keycloak and optional workers are disabled unless requested

App Configuration defaults to `standard`. The Free SKU's daily request quota is too small for reliable cold starts across the API, SignalR, Engine, Dispatcher, and Function workers, especially during a deployment when several instances start together. A very small one-service experiment can explicitly set `app_configuration_sku = "free"`, but quota exhaustion presents as slow `AzureAppConfigurationProvider.Load()` timeouts and failed Function trigger synchronization rather than a clear startup error.

Cosmos free tier can only be enabled when creating the Cosmos account. Azure SQL free-offer settings are subscription/region/SKU constrained and are left as documented variables rather than preview-only Terraform attributes; the default uses the stable `Basic` database SKU. Private endpoints add complexity and cost, so `enable_private_endpoints=false` is the default.

## Images And Deployment

Terraform accepts prebuilt image references such as:

```hcl
api_image               = "<acr>.azurecr.io/workflow-builder/backend-api:<tag>"
signalr_image           = "<acr>.azurecr.io/workflow-builder/backend-signalr:<tag>"
engine_image            = "<acr>.azurecr.io/workflow-builder/backend-engine:<tag>"
dispatcher_image        = "<acr>.azurecr.io/workflow-builder/backend-dispatcher:<tag>"
projection_worker_image = "<acr>.azurecr.io/workflow-builder/backend-projection-worker:<tag>"
maintenance_image       = "<acr>.azurecr.io/workflow-builder/backend-maintenance:<tag>"
```

The default image values are harmless placeholders so service shells can be created before your image deployment route is ready. `.github/workflows/build-service-images.yml` builds and pushes Linux images for the backend service Dockerfiles to ACR using Azure OIDC. Configure `ACR_NAME`, `ACR_LOGIN_SERVER`, `IMAGE_NAMESPACE`, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID` as GitHub repository/environment variables, or pass matching workflow inputs when running it manually or as a reusable workflow. The Azure federated identity needs `AcrPush` on the target registry.

The image workflow can also publish `backend-progress-broadcaster` and `backend-plugin-detector` images for future containerized worker routes by setting `include_function_images=true`. The current Terraform Function App path does not consume those images; the demo deployment workflow publishes Function Apps as zip packages instead.

`.github/workflows/deploy-azure-demo.yml` is the end-to-end demo deployment route for this Terraform layout:

- builds and pushes Container App images for API, SignalR, Engine, Dispatcher, and enabled worker Container Apps
- updates the matching Azure Container Apps to those image tags
- builds and uploads the Angular Static Web App when enabled
- builds canonical plugin bundles with `BuildScripts/bundle-plugins.ps1` and uploads them to the `plugins` blob container when enabled
- deploys ProgressBroadcaster and PluginDetector as Function App zip packages when those Terraform resources are enabled
- optionally runs `Backend.Database.MigrationService` using Azure App Configuration bootstrap values

After `terraform apply`, the quickest setup path is:

```powershell
pwsh ./scripts/configure-github-deployment.ps1 `
  -Repository "<owner>/<repo>" `
  -AzureClientId "<federated-identity-client-id>" `
  -AzureTenantId "<tenant-id>" `
  -AzureSubscriptionId "<subscription-id>" `
  -RunWorkflow
```

Run it from `Infra/Terraform`. The script reads `terraform output -json` or `tofu output -json`, sets the GitHub Actions variables and Static Web Apps deployment secret with `gh`, and optionally starts `deploy-azure-demo.yml`. Use `-WhatIf` to preview the `gh` operations, `-SkipStaticWebAppToken` if you want to manage the Static Web Apps token yourself, and `-RunDatabaseMigrations` if the GitHub OIDC identity has Key Vault access.

If the script reports a missing Terraform output after you pull workflow changes, run `tofu apply -refresh-only` or a normal `tofu apply` first so the local state includes the latest output definitions.

Set these GitHub repository or environment variables from Terraform outputs:

```text
AZURE_RESOURCE_GROUP                  = resource_group_name
ACR_NAME                              = container_registry_name
ACR_LOGIN_SERVER                      = container_registry_login_server
BACKEND_API_CONTAINER_APP_NAME        = container_app_names.api
BACKEND_SIGNALR_CONTAINER_APP_NAME    = container_app_names.signalr
BACKEND_ENGINE_CONTAINER_APP_NAME     = container_app_names.engine
BACKEND_DISPATCHER_CONTAINER_APP_NAME = container_app_names.dispatcher
AZURE_STORAGE_ACCOUNT_NAME            = storage_account_name
PLUGINS_CONTAINER_NAME                = plugins_container_name
AZURE_APP_CONFIGURATION_ENDPOINT      = app_configuration_endpoint
AZURE_KEY_VAULT_NAME                  = key_vault_name
```

Optional services use these variables when enabled:

```text
BACKEND_PROJECTION_WORKER_CONTAINER_APP_NAME = container_app_names.projection_worker
BACKEND_MAINTENANCE_CONTAINER_APP_NAME       = container_app_names.maintenance
PROGRESS_BROADCASTER_FUNCTION_APP_NAME       = function_app_names.progress_broadcaster
PLUGIN_DETECTOR_FUNCTION_APP_NAME            = function_app_names.plugin_detector
```

Set `AZURE_STATIC_WEB_APPS_API_TOKEN` from the sensitive `static_web_app_deployment_token` output if `DEPLOY_STATIC_WEB_APP=true`. The workflow also accepts all of these values as manual inputs, which is useful for a one-off demo deployment without storing repo variables.

The GitHub OIDC identity used by the workflows needs enough access to update the provisioned resources:

- `AcrPush` on the target ACR
- permission to update Container Apps in the resource group, such as Container Apps Contributor or Contributor scoped to the demo resource group
- `Storage Blob Data Contributor` on the storage account when uploading plugin bundles
- Function App deployment permissions if ProgressBroadcaster or PluginDetector deployment is enabled
- `Key Vault Secrets User` if database migrations are run from GitHub

Set `deployment_principal_object_id` to the Entra object id of the GitHub OIDC deployment service principal to let Terraform grant the deployment data-plane roles. The migration job reads only the SQL and Cosmos connection strings from Key Vault through the authenticated Azure CLI and passes them to the migration service through normal environment configuration. Deployed services continue to load their full configuration from Azure App Configuration and resolve Key Vault references at runtime.

Static Web Apps is provisioned but the Angular app is not uploaded by Terraform. Deploy the UI separately, then use the `frontend_url`, `api_url`, and `signalr_url` outputs to verify runtime config from `/api/client-config`.

The GitHub deployment writes `runtime-bootstrap.json` into the Angular build from the deployed Backend.API Container App FQDN. Angular reads that small same-origin file first, then retrieves the full public client configuration from Backend.API. Terraform configures the provisioned Static Web App origin for API and SignalR CORS automatically; set `frontend_allowed_origin` only when a custom frontend domain should replace it.

## Configuration

Services get only a small bootstrap environment:

- `AzureAppConfiguration__Enabled`
- `AzureAppConfiguration__Endpoint`
- `AzureAppConfiguration__Label`
- `AzureAppConfiguration__ManagedIdentityClientId`
- runtime environment and port values where needed for Container Apps

Azure Functions default `functions_use_app_configuration=false`. Flex Consumption gives the worker a short host-initialization window, so synchronously loading App Configuration and its Key Vault references can prevent trigger synchronization. Terraform therefore supplies only each Function's required settings through Function App settings, which the existing environment configuration provider reads normally. Set `functions_use_app_configuration=true` only when startup latency has been proven to fit the Flex limit. Trigger binding values such as `RealtimeCosmos` and `PluginsBlobContainerName` must remain Function App settings because the Functions host resolves them before the isolated worker starts.

Each Flex Function App has its own deployment package container. Do not point multiple apps at one deployment container: deployment history and active package state are scoped through that storage location, and sharing it can cause one app to observe the other app's deployments.

The app loads Azure App Configuration before topology validation, then re-adds environment variables as the final provider. That means ACA and Function App environment variables should stay minimal: App Configuration bootstrap values, managed identity client id, platform-required values such as `AzureWebJobsStorage`, and emergency overrides only. Normal app settings should live in Azure App Configuration.

Non-secret settings are stored directly with the environment label. Secrets are stored in Key Vault and referenced from App Configuration when `enable_key_vault=true`; otherwise the demo stores secret values directly in App Configuration.

The current app runtime still uses connection strings for SQL, Cosmos, Storage, and Service Bus. The Terraform keeps those as secrets and includes TODOs in comments/docs for later managed-identity data-plane clients.

## Region And Trial Subscription Notes

The demo example uses `ukwest` for the primary resources and `westeurope` for Static Web Apps because Azure Static Web Apps is not available in every Azure region, including `uksouth` and `ukwest` at the time this Terraform was written. If you choose a different primary region, keep `static_web_app_location` set to one of the supported Static Web Apps regions.

Azure SQL and Cosmos DB can be more tightly restricted on trial/free subscriptions than Container Apps or Storage. The demo example keeps the primary app resources in `ukwest` but sets `sql_location` and `cosmos_location` to `swedencentral` so data-plane capacity can be adjusted independently when Azure reports regional provisioning restrictions.

The demo profile enables ProgressBroadcaster and PluginDetector as .NET isolated Flex Consumption Function Apps. Flex uses the Function App resource runtime settings and a deployment blob container, so do not add classic `FUNCTIONS_WORKER_RUNTIME` or `WEBSITE_RUN_FROM_PACKAGE` app settings in Terraform. The Azure Flex runtime identifier must be the full `10.0` value; Azure accepts `10` while provisioning but the resulting host cannot initialize or synchronize triggers. Some free/trial subscriptions can still restrict App Service/Functions quota; set `enable_progress_broadcaster=false` and `enable_plugin_detector=false` if quota is unavailable.

## Security Notes

Only API, SignalR, frontend, and optional Keycloak are public. Worker Container Apps have no external ingress.

`backend-signalr` hosts the public hub and internal broadcaster endpoints in the same app. The internal routes require the `workflowbuilder.internal_publisher` role when authentication is enabled, and a generated `X-WorkflowBuilder-Internal-Publisher` shared secret can be used for auth-disabled demos.

Private endpoint support is scaffolded by variables but not fully implemented in this first pass. Service Bus private endpoints require Premium; the cheap demo uses Basic with public endpoints plus secrets/auth instead of silently changing SKU.

Secrets, keys, connection strings, Static Web App deployment token, Keycloak admin password, and the internal publisher secret are marked sensitive outputs or not output at all. Do not commit `.terraform`, state, plan files, or populated tfvars containing secrets.

## Engine Hosting

`engine_hosting_mode = "aca_worker"` is the only active implementation. It creates `backend-engine` as an internal Azure Container App worker with configurable min/max replicas and optional Service Bus queue scaling.

`aca_job` is reserved for a future finite queue-triggered engine job model. `batch`, `aci`, AKS, VMs, and VMSS are out of scope.

## Run

```powershell
cd Infra/Terraform
terraform init
terraform fmt -recursive
terraform validate
terraform plan -var-file=environments/demo/terraform.tfvars
terraform apply -var-file=environments/demo/terraform.tfvars
```

For local state, copy `environments/demo/terraform.tfvars.example` to `environments/demo/terraform.tfvars`. For remote state, adapt `environments/demo/backend.tf.example`.

## Teardown

For demos, the simplest cleanup is:

```powershell
terraform destroy -var-file=environments/demo/terraform.tfvars
```

If the whole resource group is dedicated to the demo, deleting the resource group is also acceptable.

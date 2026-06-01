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
- Azure App Configuration Free for shared app settings
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

Cosmos free tier can only be enabled when creating the Cosmos account. Azure SQL free-offer settings are subscription/region/SKU constrained and are left as documented variables rather than preview-only Terraform attributes; the default uses the stable `Basic` database SKU. Private endpoints add complexity and cost, so `enable_private_endpoints=false` is the default.

## Images And Deployment

Terraform accepts prebuilt image references such as:

```hcl
api_image        = "<acr>.azurecr.io/workflow-builder/backend-api:demo"
signalr_image    = "<acr>.azurecr.io/workflow-builder/backend-signalr:demo"
engine_image     = "<acr>.azurecr.io/workflow-builder/backend-engine:demo"
dispatcher_image = "<acr>.azurecr.io/workflow-builder/backend-dispatcher:demo"
```

The default image values are harmless placeholders so service shells can be created before your image deployment route is ready. Use the Dockerfiles under each service project to build/push images through CI or CLI.

Function App shells are created when enabled, but Terraform does not deploy Function code packages. Deploy `Backend.Functions.ProgressBroadcaster` and `Backend.Functions.PluginDetector` through your preferred Functions deployment route.

Static Web Apps is provisioned but the Angular app is not uploaded by Terraform. Deploy the UI separately, then use the `frontend_url`, `api_url`, and `signalr_url` outputs to verify runtime config from `/api/client-config`.

## Configuration

Services get only a small bootstrap environment:

- `AzureAppConfiguration__Enabled`
- `AzureAppConfiguration__Endpoint`
- `AzureAppConfiguration__Label`
- `AzureAppConfiguration__ManagedIdentityClientId`
- runtime environment and port values where needed

The app loads Azure App Configuration before topology validation, then re-adds environment variables as the final provider. That means ACA and Function App environment variables should stay minimal: App Configuration bootstrap values, managed identity client id, port/runtime values, and emergency overrides only. Normal app settings should live in Azure App Configuration.

Non-secret settings are stored directly with the environment label. Secrets are stored in Key Vault and referenced from App Configuration when `enable_key_vault=true`; otherwise the demo stores secret values directly in App Configuration.

The current app runtime still uses connection strings for SQL, Cosmos, Storage, and Service Bus. The Terraform keeps those as secrets and includes TODOs in comments/docs for later managed-identity data-plane clients.

## Region And Trial Subscription Notes

The demo example uses `westeurope` because Azure Static Web Apps is not available in every Azure region, including `uksouth` at the time this Terraform was written. If you choose a different primary region, keep `static_web_app_location` set to one of the supported Static Web Apps regions.

The optional Functions workers create Linux Consumption plans. Some free/trial subscriptions have App Service total VM quota set to zero, which causes Function plan creation to fail with an additional quota message. Leave `enable_progress_broadcaster=false` and `enable_plugin_detector=false` until quota is available.

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

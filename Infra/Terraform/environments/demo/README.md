# Demo Environment

Copy `terraform.tfvars.example` to `terraform.tfvars`, fill in `location`, and apply the infrastructure. The checked-in GitHub deployment workflow can then build images, upload Function packages, publish the Static Web App, and seed plugin bundles into the resources Terraform created.

```powershell
cd Infra/Terraform
terraform init
terraform plan -var-file=environments/demo/terraform.tfvars
terraform apply -var-file=environments/demo/terraform.tfvars
```

The example keeps the cheap profile on: ACA scale-to-zero, Cosmos free tier, Service Bus Basic, Static Web Apps Free, no private endpoints, no observability stack, ProgressBroadcaster and PluginDetector as .NET isolated Flex Consumption Function Apps, and no Keycloak unless explicitly enabled.

For an NFT run, copy `nft.tfvars.example` to ignored `nft.tfvars`, set `projection_worker_image` to the exact ACR image tag under test, and apply it after the normal demo file:

```powershell
tofu apply `
  -var-file=environments/demo/terraform.tfvars `
  -var-file=environments/demo/nft.tfvars
```

This is deliberately a warm, fixed single-replica profile. It enables asynchronous projections and prevents scale-to-zero cold starts from dominating the result. Restore the low-cost profile with a normal apply that omits `nft.tfvars`.

The image override selects the ProjectionWorker image when Terraform first creates it. Existing Container App images are managed by the deployment workflow, so deploy the intended image tag to all services before measuring and verify the actual revisions in the runner's `azure-deployment.json` artifact.

After `terraform apply`, copy the root outputs into GitHub repository or environment variables before running `.github/workflows/deploy-azure-demo.yml`. The important mappings are documented in `Infra/Terraform/README.md`; at minimum the workflow needs the resource group, ACR name/login server, Container App names, storage account, plugin container, and Azure OIDC values.

If your subscription has App Service/Functions quota restrictions, disabling ProgressBroadcaster removes live Process updates. Disabling PluginDetector removes automatic Plugin registration from uploaded bundles. Restore the corresponding Function App before testing either flow.

For remote state, copy `environments/demo/backend.tf.example` to `backend.tf` in the active root module (`Infra/Terraform`), fill in the state storage details, then initialize from that root.

For Keycloak-backed user authentication, provision the realm and clients as well as enabling the Keycloak and authentication flags. Verify the deployed SPA redirect URI and service audiences before exercising the UI. [Authentication](../../../../Docs/authentication-keycloak.md) · [Deployment views](../../../../Docs/architecture/deployment.md)

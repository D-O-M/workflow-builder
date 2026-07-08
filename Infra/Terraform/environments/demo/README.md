# Demo Environment

Copy `terraform.tfvars.example` to `terraform.tfvars`, fill in `location`, and apply the infrastructure. The checked-in GitHub deployment workflow can then build images, upload Function packages, publish the Static Web App, and seed plugin bundles into the resources Terraform created.

```powershell
cd Infra/Terraform
terraform init
terraform plan -var-file=environments/demo/terraform.tfvars
terraform apply -var-file=environments/demo/terraform.tfvars
```

The example keeps the cheap profile on: ACA scale-to-zero, Cosmos free tier, Service Bus Basic, Static Web Apps Free, no private endpoints, no observability stack, no Functions workers by default, and no Keycloak unless explicitly enabled.

After `terraform apply`, copy the root outputs into GitHub repository or environment variables before running `.github/workflows/deploy-azure-demo.yml`. The important mappings are documented in `Infra/Terraform/README.md`; at minimum the workflow needs the resource group, ACR name/login server, Container App names, storage account, plugin container, and Azure OIDC values.

If your subscription has App Service/Functions quota, you can enable the realtime progress broadcaster later with `enable_progress_broadcaster=true`. Some trial subscriptions report `Current Limit (Total VMs): 0` when creating a Linux Consumption Function plan; keep it disabled in that case.

`backend.tf.example` is optional. Copy it to this folder or the root Terraform folder only when you want remote state.

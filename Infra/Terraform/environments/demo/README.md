# Demo Environment

Copy `terraform.tfvars.example` to `terraform.tfvars`, fill in `location`, and replace image references after your external image build route publishes service containers.

```powershell
cd Infra/Terraform
terraform init
terraform plan -var-file=environments/demo/terraform.tfvars
terraform apply -var-file=environments/demo/terraform.tfvars
```

The example keeps the cheap profile on: ACA scale-to-zero, Cosmos free tier, Service Bus Basic, Static Web Apps Free, no private endpoints, no observability stack, and no Keycloak unless explicitly enabled.

`backend.tf.example` is optional. Copy it to this folder or the root Terraform folder only when you want remote state.

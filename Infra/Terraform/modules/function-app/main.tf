resource "azurerm_service_plan" "this" {
  name                = var.plan_name
  resource_group_name = var.resource_group_name
  location            = var.location
  os_type             = "Linux"
  sku_name            = "Y1"
  tags                = var.tags
}

resource "azurerm_linux_function_app" "this" {
  name                            = var.name
  resource_group_name             = var.resource_group_name
  location                        = var.location
  service_plan_id                 = azurerm_service_plan.this.id
  storage_account_name            = var.storage_account_name
  storage_account_access_key      = var.storage_account_access_key
  https_only                      = true
  key_vault_reference_identity_id = var.key_vault_reference_identity_id
  app_settings                    = var.app_settings
  tags                            = var.tags

  lifecycle {
    ignore_changes = [
      zip_deploy_file
    ]
  }

  dynamic "identity" {
    for_each = length(var.identity_ids) == 0 ? [] : [1]
    content {
      type         = "UserAssigned"
      identity_ids = var.identity_ids
    }
  }

  site_config {
    application_stack {
      dotnet_version              = var.dotnet_version
      use_dotnet_isolated_runtime = true
    }
  }
}

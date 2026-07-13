resource "azurerm_service_plan" "this" {
  name                = var.plan_name
  resource_group_name = var.resource_group_name
  location            = var.location
  os_type             = "Linux"
  sku_name            = "FC1"
  tags                = var.tags
}

resource "azurerm_function_app_flex_consumption" "this" {
  name                        = var.name
  resource_group_name         = var.resource_group_name
  location                    = var.location
  service_plan_id             = azurerm_service_plan.this.id
  storage_container_endpoint  = var.storage_container_endpoint
  storage_container_type      = "blobContainer"
  storage_authentication_type = "StorageAccountConnectionString"
  storage_access_key          = var.storage_account_access_key
  https_only                  = true
  app_settings                = var.app_settings
  runtime_name                = "dotnet-isolated"
  runtime_version             = var.dotnet_version
  maximum_instance_count      = var.maximum_instance_count
  instance_memory_in_mb       = var.instance_memory_in_mb
  tags                        = var.tags

  lifecycle {
    ignore_changes = [
      app_settings["AzureWebJobsStorage"],
      app_settings["DEPLOYMENT_STORAGE_CONNECTION_STRING"],
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
  }
}

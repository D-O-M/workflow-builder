data "azurerm_client_config" "current" {}

resource "random_string" "suffix" {
  length  = 5
  lower   = true
  upper   = false
  numeric = true
  special = false
}

resource "random_password" "sql_admin_password" {
  length           = 24
  special          = true
  override_special = "!#$%&*()-_=+[]:,.?"
}

resource "random_password" "keycloak_admin_password" {
  length  = 24
  special = true
}

resource "random_password" "internal_publisher_secret" {
  length  = 32
  special = false
}

module "naming" {
  source           = "./modules/naming"
  project_name     = var.project_name
  environment_name = var.environment_name
  suffix           = var.random_suffix_enabled ? random_string.suffix.result : ""
}

module "resource_group" {
  source   = "./modules/resource-group"
  create   = var.create_resource_group
  name     = var.resource_group_name != "" ? var.resource_group_name : module.naming.resource_group_name
  location = var.location
  tags     = local.tags
}

module "log_analytics" {
  source              = "./modules/log-analytics"
  name                = module.naming.log_analytics_name
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  tags                = local.tags
}

module "networking" {
  source                        = "./modules/networking"
  name                          = module.naming.vnet_name
  resource_group_name           = module.resource_group.name
  location                      = module.resource_group.location
  address_space                 = var.vnet_address_space
  container_apps_subnet_cidr    = var.container_apps_subnet_cidr
  private_endpoints_subnet_cidr = var.private_endpoints_subnet_cidr
  tags                          = local.tags
}

module "identities" {
  source              = "./modules/managed-identity"
  identities          = module.naming.identities
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  tags                = local.tags
}

module "container_registry" {
  count               = var.enable_container_registry ? 1 : 0
  source              = "./modules/container-registry"
  name                = module.naming.container_registry_name
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  tags                = local.tags
}

module "container_app_environment" {
  source                     = "./modules/container-app-environment"
  name                       = module.naming.container_app_environment_name
  resource_group_name        = module.resource_group.name
  location                   = module.resource_group.location
  log_analytics_workspace_id = module.log_analytics.id
  infrastructure_subnet_id   = module.networking.container_apps_subnet_id
  tags                       = local.tags
}

module "app_configuration" {
  count               = var.enable_app_configuration ? 1 : 0
  source              = "./modules/app-configuration"
  name                = module.naming.app_configuration_name
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  sku                 = lower(var.app_configuration_sku)
  tags                = local.tags
}

module "sql" {
  source                        = "./modules/sql"
  server_name                   = module.naming.sql_server_name
  database_name                 = var.sql_database_name
  resource_group_name           = module.resource_group.name
  location                      = var.sql_location != "" ? var.sql_location : module.resource_group.location
  administrator_login           = var.sql_admin_username
  administrator_password        = local.sql_admin_password
  sku_name                      = var.sql_sku_name
  free_offer_mode_enabled       = var.sql_free_offer_mode_enabled
  allow_azure_services          = true
  public_network_access_enabled = true
  tags                          = local.tags
}

module "cosmos" {
  source              = "./modules/cosmos"
  account_name        = module.naming.cosmos_account_name
  database_name       = var.cosmos_database_name
  resource_group_name = module.resource_group.name
  location            = var.cosmos_location != "" ? var.cosmos_location : module.resource_group.location
  free_tier_enabled   = var.cosmos_free_tier_enabled
  serverless_enabled  = var.cosmos_enable_serverless
  containers          = local.cosmos_containers
  tags                = local.tags
}

module "storage" {
  source              = "./modules/storage"
  account_name        = module.naming.storage_account_name
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  replication_type    = var.storage_replication_type
  containers          = local.blob_containers
  tags                = local.tags
}

module "service_bus" {
  source              = "./modules/service-bus"
  namespace_name      = module.naming.service_bus_namespace_name
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  sku                 = var.service_bus_sku
  queues              = local.service_bus_queues
  tags                = local.tags
}

module "key_vault" {
  count                = var.enable_key_vault ? 1 : 0
  source               = "./modules/key-vault"
  name                 = module.naming.key_vault_name
  resource_group_name  = module.resource_group.name
  location             = module.resource_group.location
  tenant_id            = data.azurerm_client_config.current.tenant_id
  current_principal_id = data.azurerm_client_config.current.object_id
  secrets              = local.key_vault_secret_values
  tags                 = local.tags
}

module "role_assignments" {
  source = "./modules/role-assignments"
  assignments = merge(
    var.enable_app_configuration ? {
      for key, principal_id in module.identities.principal_ids :
      "app-config-data-reader-${key}" => {
        scope                = module.app_configuration[0].id
        role_definition_name = "App Configuration Data Reader"
        principal_id         = principal_id
      }
    } : {},
    var.enable_app_configuration ? {
      "app-config-data-owner-current" = {
        scope                = module.app_configuration[0].id
        role_definition_name = "App Configuration Data Owner"
        principal_id         = data.azurerm_client_config.current.object_id
      }
    } : {},
    var.enable_app_configuration && trimspace(var.deployment_principal_object_id) != "" ? {
      "app-config-data-reader-deployment" = {
        scope                = module.app_configuration[0].id
        role_definition_name = "App Configuration Data Reader"
        principal_id         = var.deployment_principal_object_id
      }
    } : {},
    var.enable_key_vault ? {
      for key, principal_id in module.identities.principal_ids :
      "key-vault-secrets-user-${key}" => {
        scope                = module.key_vault[0].id
        role_definition_name = "Key Vault Secrets User"
        principal_id         = principal_id
      }
    } : {},
    var.enable_key_vault && trimspace(var.deployment_principal_object_id) != "" ? {
      "key-vault-secrets-user-deployment" = {
        scope                = module.key_vault[0].id
        role_definition_name = "Key Vault Secrets User"
        principal_id         = var.deployment_principal_object_id
      }
    } : {},
    var.enable_container_registry ? {
      for key, principal_id in module.identities.principal_ids :
      "acr-pull-${key}" => {
        scope                = module.container_registry[0].id
        role_definition_name = "AcrPull"
        principal_id         = principal_id
      }
    } : {}
  )
}

resource "azurerm_app_configuration_key" "settings" {
  for_each               = var.enable_app_configuration ? local.app_configuration_settings : {}
  configuration_store_id = module.app_configuration[0].id
  key                    = each.key
  label                  = var.environment_name
  value                  = each.value

  depends_on = [module.role_assignments]
}

resource "azurerm_app_configuration_key" "secret_references" {
  for_each               = var.enable_app_configuration && var.enable_key_vault ? local.key_vault_secret_names : {}
  configuration_store_id = module.app_configuration[0].id
  key                    = each.key
  label                  = var.environment_name
  content_type           = "application/vnd.microsoft.appconfig.keyvaultref+json;charset=utf-8"
  value                  = jsonencode({ uri = module.key_vault[0].secret_ids[each.value] })

  depends_on = [module.role_assignments, module.key_vault]

  lifecycle {
    ignore_changes = [
      type,
      vault_key_reference
    ]
  }
}

resource "azurerm_app_configuration_key" "secret_values" {
  for_each               = var.enable_app_configuration && !var.enable_key_vault ? toset(keys(local.app_configuration_secret_values)) : toset([])
  configuration_store_id = module.app_configuration[0].id
  key                    = each.key
  label                  = var.environment_name
  value                  = local.app_configuration_secret_values[each.key]

  depends_on = [module.role_assignments]
}

resource "terraform_data" "process_recovery_prerequisites" {
  count = var.enable_process_recovery ? 1 : 0

  lifecycle {
    precondition {
      condition     = var.authentication_enabled
      error_message = "Enable product authentication before enabling Process recovery."
    }
    precondition {
      condition     = var.enable_engine_queue_scaling || var.engine_min_replicas >= 1
      error_message = "Process recovery requires Engine queue scaling or at least one running Engine replica."
    }
  }
}

module "api" {
  depends_on                   = [terraform_data.process_recovery_prerequisites]
  source                       = "./modules/container-app"
  name                         = module.naming.container_apps.api
  resource_group_name          = module.resource_group.name
  container_app_environment_id = module.container_app_environment.id
  image                        = var.api_image
  container_name               = "backend-api"
  cpu                          = var.default_cpu
  memory                       = var.default_memory
  min_replicas                 = var.api_min_replicas
  max_replicas                 = var.api_max_replicas
  identity_ids                 = [module.identities.ids.api]
  registry_server              = startswith(var.api_image, "${local.acr_login_server}/") ? local.acr_login_server : null
  registry_identity_id         = startswith(var.api_image, "${local.acr_login_server}/") ? module.identities.ids.api : null
  ingress_external_enabled     = true
  ingress_target_port          = var.container_port
  env                          = local.app_config_env_by_service.api
  tags                         = local.tags
}

module "signalr" {
  source                       = "./modules/container-app"
  name                         = module.naming.container_apps.signalr
  resource_group_name          = module.resource_group.name
  container_app_environment_id = module.container_app_environment.id
  image                        = var.signalr_image
  container_name               = "backend-signalr"
  cpu                          = var.default_cpu
  memory                       = var.default_memory
  min_replicas                 = var.signalr_min_replicas
  max_replicas                 = var.signalr_max_replicas
  identity_ids                 = [module.identities.ids.signalr]
  registry_server              = startswith(var.signalr_image, "${local.acr_login_server}/") ? local.acr_login_server : null
  registry_identity_id         = startswith(var.signalr_image, "${local.acr_login_server}/") ? module.identities.ids.signalr : null
  ingress_external_enabled     = true
  ingress_target_port          = var.container_port
  env                          = local.app_config_env_by_service.signalr
  tags                         = local.tags
}

module "engine" {
  source                       = "./modules/container-app-worker"
  name                         = module.naming.container_apps.engine
  resource_group_name          = module.resource_group.name
  container_app_environment_id = module.container_app_environment.id
  image                        = var.engine_image
  container_name               = "backend-engine"
  cpu                          = var.engine_cpu
  memory                       = var.engine_memory
  min_replicas                 = var.engine_min_replicas
  max_replicas                 = var.engine_max_replicas
  identity_ids                 = [module.identities.ids.engine]
  registry_server              = startswith(var.engine_image, "${local.acr_login_server}/") ? local.acr_login_server : null
  registry_identity_id         = startswith(var.engine_image, "${local.acr_login_server}/") ? module.identities.ids.engine : null
  env                          = local.app_config_env_by_service.engine
  secret_values = {
    "servicebus-connection-string" = module.service_bus.primary_connection_string
  }
  custom_scale_rules = var.enable_engine_queue_scaling ? [
    {
      name             = "process-step-queue"
      custom_rule_type = "azure-servicebus"
      metadata = {
        queueName    = "processsteps"
        namespace    = module.service_bus.namespace_name
        messageCount = tostring(var.process_step_queue_scale_threshold)
      }
      authentication = [
        {
          secret_name       = "servicebus-connection-string"
          trigger_parameter = "connection"
        }
      ]
    }
  ] : []
  tags = local.tags
}

module "dispatcher" {
  depends_on                   = [terraform_data.process_recovery_prerequisites]
  source                       = "./modules/container-app-worker"
  name                         = module.naming.container_apps.dispatcher
  resource_group_name          = module.resource_group.name
  container_app_environment_id = module.container_app_environment.id
  image                        = var.dispatcher_image
  container_name               = "backend-dispatcher"
  cpu                          = var.default_cpu
  memory                       = var.default_memory
  min_replicas                 = max(var.dispatcher_min_replicas, var.enable_process_recovery ? 1 : 0)
  max_replicas                 = var.dispatcher_max_replicas
  identity_ids                 = [module.identities.ids.dispatcher]
  registry_server              = startswith(var.dispatcher_image, "${local.acr_login_server}/") ? local.acr_login_server : null
  registry_identity_id         = startswith(var.dispatcher_image, "${local.acr_login_server}/") ? module.identities.ids.dispatcher : null
  env                          = local.app_config_env_by_service.dispatcher
  secret_values = {
    "servicebus-connection-string" = module.service_bus.primary_connection_string
  }
  custom_scale_rules = [
    {
      name             = "process-step-dispatches"
      custom_rule_type = "azure-servicebus"
      metadata = {
        queueName    = "processsteps"
        namespace    = module.service_bus.namespace_name
        messageCount = tostring(var.process_step_queue_scale_threshold)
      }
      authentication = [
        {
          secret_name       = "servicebus-connection-string"
          trigger_parameter = "connection"
        }
      ]
    }
  ]
  tags = local.tags
}

module "projection_worker" {
  count                        = var.enable_projection_worker ? 1 : 0
  source                       = "./modules/container-app-worker"
  name                         = module.naming.container_apps.projection_worker
  resource_group_name          = module.resource_group.name
  container_app_environment_id = module.container_app_environment.id
  image                        = var.projection_worker_image
  container_name               = "backend-projection-worker"
  cpu                          = var.default_cpu
  memory                       = var.default_memory
  min_replicas                 = var.projection_worker_min_replicas
  max_replicas                 = var.projection_worker_max_replicas
  identity_ids                 = [module.identities.ids.projection_worker]
  registry_server              = startswith(var.projection_worker_image, "${local.acr_login_server}/") ? local.acr_login_server : null
  registry_identity_id         = startswith(var.projection_worker_image, "${local.acr_login_server}/") ? module.identities.ids.projection_worker : null
  env                          = local.app_config_env_by_service.projection_worker
  tags                         = local.tags
}

module "maintenance" {
  count                        = var.enable_maintenance_worker ? 1 : 0
  source                       = "./modules/container-app-worker"
  name                         = module.naming.container_apps.maintenance
  resource_group_name          = module.resource_group.name
  container_app_environment_id = module.container_app_environment.id
  image                        = var.maintenance_image
  container_name               = "backend-maintenance"
  cpu                          = var.default_cpu
  memory                       = var.default_memory
  min_replicas                 = 0
  max_replicas                 = 1
  identity_ids                 = [module.identities.ids.maintenance]
  registry_server              = startswith(var.maintenance_image, "${local.acr_login_server}/") ? local.acr_login_server : null
  registry_identity_id         = startswith(var.maintenance_image, "${local.acr_login_server}/") ? module.identities.ids.maintenance : null
  env                          = local.app_config_env_by_service.maintenance
  tags                         = local.tags
}

module "keycloak" {
  count                        = var.enable_keycloak ? 1 : 0
  source                       = "./modules/keycloak"
  name                         = module.naming.container_apps.keycloak
  resource_group_name          = module.resource_group.name
  container_app_environment_id = module.container_app_environment.id
  image                        = var.keycloak_image
  admin_username               = var.keycloak_admin_username
  admin_password               = local.keycloak_admin_password
  realm                        = var.keycloak_realm
  identity_ids                 = [module.identities.ids.keycloak]
  tags                         = local.tags
}

module "static_web_app" {
  count               = var.enable_static_web_app ? 1 : 0
  source              = "./modules/static-web-app"
  name                = module.naming.static_web_app_name
  resource_group_name = module.resource_group.name
  location            = var.static_web_app_location
  tags                = local.tags
}

module "progress_broadcaster_function" {
  count                      = var.enable_progress_broadcaster ? 1 : 0
  source                     = "./modules/function-app"
  name                       = module.naming.function_apps.progress_broadcaster
  plan_name                  = "${module.naming.function_apps.progress_broadcaster}-flex-plan"
  resource_group_name        = module.resource_group.name
  location                   = module.resource_group.location
  storage_account_access_key = module.storage.primary_access_key
  storage_container_endpoint = "https://${module.storage.account_name}.blob.core.windows.net/${module.storage.container_names["progress-function-releases"]}"
  identity_ids               = [module.identities.ids.progress_broadcaster]
  app_settings = merge(local.function_app_config_env_base, local.progress_broadcaster_function_env, {
    "AzureAppConfiguration__ManagedIdentityClientId" = module.identities.client_ids.progress_broadcaster
    "DEPLOYMENT_STORAGE_CONNECTION_STRING"           = module.storage.primary_connection_string
    "AzureWebJobsStorage"                            = module.storage.primary_connection_string
  })
  tags = local.tags
}

module "plugin_detector_function" {
  count                      = var.enable_plugin_detector ? 1 : 0
  source                     = "./modules/function-app"
  name                       = module.naming.function_apps.plugin_detector
  plan_name                  = "${module.naming.function_apps.plugin_detector}-flex-plan"
  resource_group_name        = module.resource_group.name
  location                   = module.resource_group.location
  storage_account_access_key = module.storage.primary_access_key
  storage_container_endpoint = "https://${module.storage.account_name}.blob.core.windows.net/${module.storage.container_names["plugin-function-releases"]}"
  identity_ids               = [module.identities.ids.plugin_detector]
  app_settings = merge(local.function_app_config_env_base, local.plugin_detector_function_env, {
    "AzureAppConfiguration__ManagedIdentityClientId" = module.identities.client_ids.plugin_detector
    "DEPLOYMENT_STORAGE_CONNECTION_STRING"           = module.storage.primary_connection_string
    "AzureWebJobsStorage"                            = module.storage.primary_connection_string
  })
  tags = local.tags
}

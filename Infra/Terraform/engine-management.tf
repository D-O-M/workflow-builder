locals {
  management_env = concat(
    [{ name = "EngineManagement__Enabled", value = tostring(var.enable_engine_management) }],
    var.enable_engine_management ? [{ name = "EngineManagement__VaultUri", value = azurerm_key_vault.plugin_secrets[0].vault_uri }] : []
  )
}

resource "azurerm_key_vault" "plugin_secrets" {
  count                      = var.enable_engine_management ? 1 : 0
  name                       = "wb-plugins-${random_string.suffix.result}"
  location                   = module.resource_group.location
  resource_group_name        = module.resource_group.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  soft_delete_retention_days = 7
  tags                       = local.tags

  lifecycle {
    precondition {
      condition     = var.enable_keycloak && var.enable_app_configuration
      error_message = "Configure Keycloak and App Configuration before enabling Engine management."
    }
  }
}

resource "azurerm_role_definition" "plugin_secret_writer" {
  count       = var.enable_engine_management ? 1 : 0
  name        = "WorkflowBuilder Plugin secret writer ${random_string.suffix.result}"
  description = "Write new versions of managed Plugin credentials."
  scope       = module.resource_group.id
  permissions {
    data_actions = ["Microsoft.KeyVault/vaults/secrets/setSecret/action"]
  }
  assignable_scopes = [module.resource_group.id]
}

resource "azurerm_role_assignment" "plugin_secret_writer" {
  count              = var.enable_engine_management ? 1 : 0
  scope              = azurerm_key_vault.plugin_secrets[0].id
  role_definition_id = azurerm_role_definition.plugin_secret_writer[0].role_definition_resource_id
  principal_id       = module.identities.principal_ids.api
}

resource "azurerm_role_assignment" "plugin_secret_reader" {
  count                = var.enable_engine_management ? 1 : 0
  scope                = azurerm_key_vault.plugin_secrets[0].id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = module.identities.principal_ids.engine
}

resource "azurerm_container_app_job" "resume_steps" {
  count                        = var.enable_engine_management ? 1 : 0
  name                         = "wb-resume-steps-${random_string.suffix.result}"
  location                     = module.resource_group.location
  resource_group_name          = module.resource_group.name
  container_app_environment_id = module.container_app_environment.id
  replica_timeout_in_seconds   = 300
  replica_retry_limit          = 1
  tags = merge(local.tags, {
    workflowbuilder_role       = "engine-management-recovery"
    workflowbuilder_dispatcher = module.naming.container_apps.dispatcher
  })

  identity {
    type         = "UserAssigned"
    identity_ids = [module.identities.ids.dispatcher]
  }

  dynamic "registry" {
    for_each = var.enable_container_registry ? [local.acr_login_server] : []
    content {
      server   = registry.value
      identity = module.identities.ids.dispatcher
    }
  }

  schedule_trigger_config {
    cron_expression          = var.engine_management_recovery_cron
    parallelism              = 1
    replica_completion_count = 1
  }

  template {
    container {
      name   = "backend-dispatcher"
      image  = var.dispatcher_image
      args   = ["--resume-waiting-steps"]
      cpu    = var.default_cpu
      memory = var.default_memory
      dynamic "env" {
        for_each = local.app_config_env_by_service.dispatcher
        content {
          name  = env.value.name
          value = env.value.value
        }
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }
}

output "resource_group_name" {
  value = module.resource_group.name
}

output "container_app_environment_name" {
  value = module.container_app_environment.name
}

output "container_registry_name" {
  value = var.enable_container_registry ? module.container_registry[0].name : null
}

output "container_registry_login_server" {
  value = var.enable_container_registry ? module.container_registry[0].login_server : null
}

output "container_app_names" {
  value = {
    api               = module.api.name
    signalr           = module.signalr.name
    engine            = module.engine.name
    dispatcher        = module.dispatcher.name
    projection_worker = var.enable_projection_worker ? module.projection_worker[0].name : null
    maintenance       = var.enable_maintenance_worker ? module.maintenance[0].name : null
  }
}

output "api_url" {
  value = module.api.url
}

output "signalr_url" {
  value = module.signalr.url
}

output "frontend_url" {
  value = var.enable_static_web_app ? module.static_web_app[0].url : null
}

output "static_web_app_name" {
  value = var.enable_static_web_app ? module.static_web_app[0].name : null
}

output "keycloak_url" {
  value = var.enable_keycloak ? module.keycloak[0].url : null
}

output "keycloak_authority" {
  value = var.enable_keycloak ? module.keycloak[0].authority : null
}

output "sql_server_fqdn" {
  value = module.sql.server_fqdn
}

output "sql_database_name" {
  value = module.sql.database_name
}

output "cosmos_endpoint" {
  value = module.cosmos.endpoint
}

output "cosmos_database_name" {
  value = module.cosmos.database_name
}

output "storage_account_name" {
  value = module.storage.account_name
}

output "plugins_container_name" {
  value = module.storage.container_names["plugins"]
}

output "function_app_names" {
  value = {
    progress_broadcaster = var.enable_progress_broadcaster ? module.progress_broadcaster_function[0].name : null
    plugin_detector      = var.enable_plugin_detector ? module.plugin_detector_function[0].name : null
  }
}

output "artifacts_container_name" {
  value = module.storage.container_names["artifacts"]
}

output "service_bus_namespace_name" {
  value = module.service_bus.namespace_name
}

output "process_step_queue_name" {
  value = module.service_bus.queue_names.process_step
}

output "plugin_control_queue_name" {
  value = module.service_bus.queue_names.plugin_control
}

output "app_configuration_endpoint" {
  value = var.enable_app_configuration ? module.app_configuration[0].endpoint : null
}

output "key_vault_name" {
  value = var.enable_key_vault ? module.key_vault[0].name : null
}

output "static_web_app_deployment_token" {
  value     = var.enable_static_web_app ? module.static_web_app[0].api_key : null
  sensitive = true
}

output "keycloak_admin_password" {
  value     = local.keycloak_admin_password
  sensitive = true
}

output "internal_publisher_shared_secret" {
  value     = random_password.internal_publisher_secret.result
  sensitive = true
}

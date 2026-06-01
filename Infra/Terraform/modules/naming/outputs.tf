output "prefix" {
  value = local.prefix
}

output "resource_group_name" {
  value = "${local.prefix}-rg"
}

output "log_analytics_name" {
  value = "${local.prefix}-logs"
}

output "vnet_name" {
  value = "${local.prefix}-vnet"
}

output "container_app_environment_name" {
  value = "${local.prefix}-aca-env"
}

output "container_registry_name" {
  value = substr("${local.compact_prefix}acr", 0, 50)
}

output "storage_account_name" {
  value = substr("${local.compact_prefix}st", 0, 24)
}

output "key_vault_name" {
  value = substr("${local.prefix}-kv", 0, 24)
}

output "app_configuration_name" {
  value = substr("${local.prefix}-appcfg", 0, 50)
}

output "sql_server_name" {
  value = substr("${local.prefix}-sql", 0, 63)
}

output "cosmos_account_name" {
  value = substr("${local.prefix}-cosmos", 0, 44)
}

output "service_bus_namespace_name" {
  value = substr("${local.prefix}-busns", 0, 50)
}

output "static_web_app_name" {
  value = substr("${local.prefix}-swa", 0, 40)
}

output "container_apps" {
  value = {
    api               = substr("${local.prefix}-api", 0, 32)
    signalr           = substr("${local.prefix}-signalr", 0, 32)
    engine            = substr("${local.prefix}-engine", 0, 32)
    dispatcher        = substr("${local.prefix}-dispatcher", 0, 32)
    projection_worker = substr("${local.prefix}-projection", 0, 32)
    maintenance       = substr("${local.prefix}-maintenance", 0, 32)
    keycloak          = substr("${local.prefix}-keycloak", 0, 32)
  }
}

output "function_apps" {
  value = {
    progress_broadcaster = substr("${local.prefix}-progress-fn", 0, 60)
    plugin_detector      = substr("${local.prefix}-plugins-fn", 0, 60)
  }
}

output "identities" {
  value = {
    api                  = substr("${local.prefix}-api-id", 0, 64)
    signalr              = substr("${local.prefix}-signalr-id", 0, 64)
    engine               = substr("${local.prefix}-engine-id", 0, 64)
    dispatcher           = substr("${local.prefix}-dispatcher-id", 0, 64)
    projection_worker    = substr("${local.prefix}-projection-id", 0, 64)
    maintenance          = substr("${local.prefix}-maintenance-id", 0, 64)
    progress_broadcaster = substr("${local.prefix}-progress-id", 0, 64)
    plugin_detector      = substr("${local.prefix}-plugins-id", 0, 64)
    keycloak             = substr("${local.prefix}-keycloak-id", 0, 64)
  }
}

variable "project_name" {
  type        = string
  default     = "workflow-builder"
  description = "Project token used for Azure resource naming."
}

variable "environment_name" {
  type        = string
  default     = "demo"
  description = "Environment label used for resource names and App Configuration labels."
}

variable "location" {
  type        = string
  description = "Azure region for the demo environment."
}

variable "tags" {
  type        = map(string)
  default     = {}
  description = "Tags applied to all supported resources."
}

variable "create_resource_group" {
  type        = bool
  default     = true
  description = "Create a resource group when true; otherwise use resource_group_name."
}

variable "resource_group_name" {
  type        = string
  default     = ""
  description = "Existing resource group name when create_resource_group is false."
}

variable "random_suffix_enabled" {
  type        = bool
  default     = true
  description = "Append a short random suffix to globally unique Azure resource names."
}

variable "enable_private_endpoints" {
  type        = bool
  default     = false
  description = "Reserved toggle for future private endpoint wiring. The cheap demo defaults to public PaaS endpoints with secrets/auth."
}

variable "enable_key_vault" {
  type        = bool
  default     = true
  description = "Create Key Vault and store secrets as Key Vault references from App Configuration."
}

variable "enable_app_configuration" {
  type        = bool
  default     = true
  description = "Create Azure App Configuration and wire services to it."
}

variable "app_configuration_sku" {
  type        = string
  default     = "standard"
  description = "Azure App Configuration SKU. Standard is the reliable default for the multi-service demo; Free can exhaust its daily request quota during deployment and cold starts."

  validation {
    condition     = contains(["free", "standard", "premium"], lower(var.app_configuration_sku))
    error_message = "app_configuration_sku must be free, standard, or premium."
  }
}

variable "functions_use_app_configuration" {
  type        = bool
  default     = false
  description = "Load Azure App Configuration synchronously in Flex Consumption workers. Disabled by default because Flex enforces a short host initialization deadline; Terraform supplies each Function's required IConfiguration values as app settings instead."
}

variable "deployment_principal_object_id" {
  type        = string
  default     = ""
  description = "Optional Entra object id for the GitHub/Azure deployment principal. When set, Terraform grants it App Configuration and Key Vault data-plane read access for migration/deploy workflows."
}

variable "enable_container_registry" {
  type        = bool
  default     = true
  description = "Create Azure Container Registry for the external image deployment route."
}

variable "enable_static_web_app" {
  type        = bool
  default     = true
  description = "Provision an Azure Static Web App shell for the Angular frontend."
}

variable "static_web_app_location" {
  type        = string
  default     = "westeurope"
  description = "Azure Static Web Apps is available only in selected regions. Keep this separate from the main resource location."
}

variable "enable_keycloak" {
  type        = bool
  default     = false
  description = "Deploy a demo Keycloak container app."
}

variable "enable_observability_stack" {
  type        = bool
  default     = false
  description = "Reserved no-op. Grafana/Loki/Tempo/Prometheus are intentionally not deployed by this Terraform."

  validation {
    condition     = var.enable_observability_stack == false
    error_message = "The Azure demo Terraform does not deploy the observability stack. Keep enable_observability_stack=false."
  }
}

variable "enable_projection_worker" {
  type    = bool
  default = false
}

variable "enable_maintenance_worker" {
  type    = bool
  default = false
}

variable "enable_plugin_detector" {
  type    = bool
  default = false
}

variable "enable_progress_broadcaster" {
  type    = bool
  default = true
}

variable "enable_blueprint_seeding_job" {
  type        = bool
  default     = false
  description = "Reserved for future ACA Job seeding support."
}

variable "enable_plugin_seeding_job" {
  type        = bool
  default     = false
  description = "Reserved for future ACA Job seeding support."
}

variable "api_image" {
  type        = string
  default     = "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest"
  description = "Prebuilt Backend.API container image. Terraform does not build or publish images."
}

variable "signalr_image" {
  type        = string
  default     = "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest"
  description = "Prebuilt Backend.SignalR container image."
}

variable "engine_image" {
  type        = string
  default     = "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest"
  description = "Prebuilt Backend.Engine container image."
}

variable "dispatcher_image" {
  type        = string
  default     = "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest"
  description = "Prebuilt Backend.Dispatcher container image."
}

variable "projection_worker_image" {
  type        = string
  default     = "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest"
  description = "Prebuilt Backend.ProjectionWorker container image."
}

variable "maintenance_image" {
  type        = string
  default     = "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest"
  description = "Prebuilt Backend.Maintenance container image."
}

variable "progress_broadcaster_image" {
  type        = string
  default     = ""
  description = "Reserved for containerized Function deployment route. The Terraform Function App shell does not use this image."
}

variable "plugin_detector_image" {
  type        = string
  default     = ""
  description = "Reserved for containerized Function deployment route. The Terraform Function App shell does not use this image."
}

variable "keycloak_image" {
  type        = string
  default     = "quay.io/keycloak/keycloak:26.0"
  description = "Keycloak image used when enable_keycloak=true."
}

variable "api_min_replicas" {
  type    = number
  default = 0
}

variable "api_max_replicas" {
  type    = number
  default = 1
}

variable "signalr_min_replicas" {
  type    = number
  default = 0
}

variable "signalr_max_replicas" {
  type    = number
  default = 1
}

variable "engine_min_replicas" {
  type    = number
  default = 0
}

variable "engine_max_replicas" {
  type    = number
  default = 1
}

variable "dispatcher_min_replicas" {
  type    = number
  default = 0
}

variable "dispatcher_max_replicas" {
  type    = number
  default = 1
}

variable "projection_worker_min_replicas" {
  type    = number
  default = 0
}

variable "projection_worker_max_replicas" {
  type    = number
  default = 1
}

variable "default_cpu" {
  type    = number
  default = 0.25
}

variable "default_memory" {
  type    = string
  default = "0.5Gi"
}

variable "engine_cpu" {
  type    = number
  default = 0.5
}

variable "engine_memory" {
  type    = string
  default = "1Gi"
}

variable "container_port" {
  type        = number
  default     = 8080
  description = "Container port used by HTTP backend images."
}

variable "engine_hosting_mode" {
  type        = string
  default     = "aca_worker"
  description = "Active engine hosting mode. Only aca_worker is implemented; aca_job is reserved for later."

  validation {
    condition     = var.engine_hosting_mode == "aca_worker"
    error_message = "Only engine_hosting_mode=\"aca_worker\" is implemented. aca_job is reserved for a future queue-triggered mode."
  }
}

variable "enable_engine_queue_scaling" {
  type    = bool
  default = true
}

variable "process_step_queue_scale_threshold" {
  type    = number
  default = 1
}

variable "authentication_enabled" {
  type    = bool
  default = false
}

variable "keycloak_realm" {
  type    = string
  default = "workflow-builder"
}

variable "keycloak_spa_client_id" {
  type    = string
  default = "workflow-builder-spa"
}

variable "keycloak_api_audience" {
  type    = string
  default = "workflow-builder-api"
}

variable "keycloak_admin_username" {
  type      = string
  default   = "admin"
  sensitive = true
}

variable "keycloak_admin_password" {
  type      = string
  default   = null
  sensitive = true
  nullable  = true
}

variable "require_https_metadata" {
  type    = bool
  default = true
}

variable "sql_database_name" {
  type    = string
  default = "TasksDB"
}

variable "sql_location" {
  type        = string
  default     = ""
  description = "Optional Azure region override for Azure SQL. Defaults to the primary location when empty."
}

variable "sql_admin_username" {
  type    = string
  default = "workflowadmin"
}

variable "sql_admin_password" {
  type      = string
  default   = null
  sensitive = true
  nullable  = true
}

variable "sql_free_offer_mode_enabled" {
  type        = bool
  default     = false
  description = "Enable Azure SQL free offer mode when supported for the subscription/region."
}

variable "sql_sku_name" {
  type    = string
  default = "Basic"
}

variable "cosmos_database_name" {
  type    = string
  default = "enginedb"
}

variable "cosmos_location" {
  type        = string
  default     = ""
  description = "Optional Azure region override for Cosmos DB. Defaults to the primary location when empty."
}

variable "cosmos_free_tier_enabled" {
  type    = bool
  default = true
}

variable "cosmos_enable_serverless" {
  type    = bool
  default = false
}

variable "storage_replication_type" {
  type    = string
  default = "LRS"
}

variable "service_bus_sku" {
  type    = string
  default = "Basic"

  validation {
    condition     = contains(["Basic", "Standard", "Premium"], var.service_bus_sku)
    error_message = "service_bus_sku must be Basic, Standard, or Premium."
  }
}

variable "vnet_address_space" {
  type    = list(string)
  default = ["10.42.0.0/16"]
}

variable "container_apps_subnet_cidr" {
  type    = string
  default = "10.42.0.0/23"
}

variable "private_endpoints_subnet_cidr" {
  type    = string
  default = "10.42.3.0/24"
}

variable "allowed_ingress_ip_ranges" {
  type        = list(string)
  default     = []
  description = "Reserved for future ingress restrictions. ACA ingress IP restrictions are not wired in this first pass."
}

variable "frontend_allowed_origin" {
  type        = string
  default     = ""
  description = "Optional frontend origin override for API and SignalR CORS. Defaults to the provisioned Static Web App URL when enabled."

  validation {
    condition     = trimspace(var.frontend_allowed_origin) == "" || can(regex("^https?://[^/]+/?$", trimspace(var.frontend_allowed_origin)))
    error_message = "frontend_allowed_origin must be empty or an HTTP(S) origin without a path."
  }
}

variable "aspnetcore_environment" {
  type    = string
  default = "Production"
}

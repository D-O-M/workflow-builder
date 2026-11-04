locals {
  tags = merge(
    {
      project     = var.project_name
      environment = var.environment_name
      managed_by  = "terraform"
    },
    var.tags
  )

  cosmos_containers = {
    processes                        = { partition_key = "/blueprintId" }
    processdirectory                 = { partition_key = "/id" }
    processsummaries                 = { partition_key = "/partitionKey" }
    processrealtimesummaries         = { partition_key = "/id" }
    processnoderealtime              = { partition_key = "/processId" }
    processsteps                     = { partition_key = "/processId" }
    processstepdispatches            = { partition_key = "/partitionKey" }
    processstepfinalizations         = { partition_key = "/id" }
    pluginingestionstatuses          = { partition_key = "/id" }
    plugintaskversionruntimestatuses = { partition_key = "/taskVersionId" }
    nodeactivations                  = { partition_key = "/processId" }
    nodejoinstates                   = { partition_key = "/processId" }
    phasedprocesses                  = { partition_key = "/phasedBlueprintDefinitionId" }
    phasecorrelations                = { partition_key = "/targetPhaseKey" }
    processstepprogressleases        = { partition_key = "/id" }
    processprojectionleases          = { partition_key = "/id" }
  }

  blob_containers = [
    "plugins",
    "artifacts",
    "function-releases",
    "progress-function-releases",
    "plugin-function-releases"
  ]

  service_bus_queues = {
    process_step   = "processsteps"
    plugin_control = "pluginreloadqueue"
  }

  sql_admin_password      = coalesce(var.sql_admin_password, random_password.sql_admin_password.result)
  keycloak_admin_password = coalesce(var.keycloak_admin_password, random_password.keycloak_admin_password.result)
  keycloak_authority      = var.enable_keycloak ? module.keycloak[0].authority : "https://keycloak.example.invalid/realms/${var.keycloak_realm}"

  app_config_endpoint = var.enable_app_configuration ? module.app_configuration[0].endpoint : ""
  acr_login_server    = var.enable_container_registry ? module.container_registry[0].login_server : ""
  frontend_origin = trimspace(var.frontend_allowed_origin) != "" ? trimsuffix(trimspace(var.frontend_allowed_origin), "/") : (
    var.enable_static_web_app ? module.static_web_app[0].url : ""
  )

  app_config_env_base = [
    {
      name  = "AzureAppConfiguration__Enabled"
      value = tostring(var.enable_app_configuration)
    },
    {
      name  = "AzureAppConfiguration__Endpoint"
      value = local.app_config_endpoint
    },
    {
      name  = "AzureAppConfiguration__Label"
      value = var.environment_name
    }
  ]

  app_config_env_by_service = {
    api = concat(local.app_config_env_base, [
      {
        name  = "AzureAppConfiguration__ManagedIdentityClientId"
        value = module.identities.client_ids.api
      },
      {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = var.aspnetcore_environment
      },
      {
        name  = "ASPNETCORE_HTTP_PORTS"
        value = tostring(var.container_port)
      },
      {
        name  = "Cors__AllowedOrigins__0"
        value = local.frontend_origin
      }
    ])
    signalr = concat(local.app_config_env_base, [
      {
        name  = "AzureAppConfiguration__ManagedIdentityClientId"
        value = module.identities.client_ids.signalr
      },
      {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = var.aspnetcore_environment
      },
      {
        name  = "ASPNETCORE_HTTP_PORTS"
        value = tostring(var.container_port)
      },
      {
        name  = "Cors__AllowedOrigins__0"
        value = local.frontend_origin
      }
    ])
    engine = concat(local.app_config_env_base, [
      {
        name  = "AzureAppConfiguration__ManagedIdentityClientId"
        value = module.identities.client_ids.engine
      },
      {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = var.aspnetcore_environment
      },
      {
        name  = "ASPNETCORE_HTTP_PORTS"
        value = tostring(var.container_port)
      },
      {
        # This deployment switch must restart Engine when projection ownership changes.
        name  = "Projections__SynchronousEngineUpdatesEnabled"
        value = tostring(!var.enable_projection_worker)
      }
    ])
    dispatcher = concat(local.app_config_env_base, [
      {
        name  = "AzureAppConfiguration__ManagedIdentityClientId"
        value = module.identities.client_ids.dispatcher
      },
      {
        name  = "DOTNET_ENVIRONMENT"
        value = var.aspnetcore_environment
      }
    ])
    projection_worker = concat(local.app_config_env_base, [
      {
        name  = "AzureAppConfiguration__ManagedIdentityClientId"
        value = module.identities.client_ids.projection_worker
      },
      {
        name  = "DOTNET_ENVIRONMENT"
        value = var.aspnetcore_environment
      }
    ])
    maintenance = concat(local.app_config_env_base, [
      {
        name  = "AzureAppConfiguration__ManagedIdentityClientId"
        value = module.identities.client_ids.maintenance
      },
      {
        name  = "DOTNET_ENVIRONMENT"
        value = var.aspnetcore_environment
      }
    ])
  }

  api_url     = module.api.url
  signalr_url = module.signalr.url

  app_configuration_settings = {
    "AzureCosmos:ConnectionName"                               = "enginedb"
    "AzureCosmos:DatabaseName"                                 = var.cosmos_database_name
    "AzureCosmos:ResourceName"                                 = module.cosmos.account_name
    "AzureStorage:BlobContainers:Plugins:ContainerName"        = "plugins"
    "AzureStorage:BlobContainers:Artifacts:ContainerName"      = "artifacts"
    "AzureServiceBus:Queues:ProcessStepQueue:ConnectionName"   = "process-step-queue"
    "AzureServiceBus:Queues:ProcessStepQueue:Name"             = "processsteps"
    "AzureServiceBus:Queues:PluginControlQueue:ConnectionName" = "plugin-control-queue"
    "AzureServiceBus:Queues:PluginControlQueue:Name"           = "pluginreloadqueue"
    "ProcessStepDispatch:Enabled"                              = "true"
    "ClientConfig:ApiBaseUrl"                                  = local.api_url
    "ClientConfig:SignalRBaseUrl"                              = local.signalr_url
    "ClientConfig:GrafanaBaseUrl"                              = "https://grafana.example.invalid"
    "Observability:LokiBaseUrl"                                = "http://127.0.0.1:3100"
    "Observability:PrometheusBaseUrl"                          = "http://127.0.0.1:9090"
    "ClientConfig:Authentication:Enabled"                      = tostring(var.authentication_enabled)
    "ClientConfig:Authentication:Authority"                    = var.authentication_enabled ? local.keycloak_authority : ""
    "ClientConfig:Authentication:ClientId"                     = var.authentication_enabled ? var.keycloak_spa_client_id : ""
    "ClientConfig:Authentication:Scope"                        = var.authentication_enabled ? "openid profile email" : ""
    "ClientConfig:Authentication:Audience"                     = var.authentication_enabled ? var.keycloak_api_audience : ""
    "Cors:AllowedOrigins:0"                                    = local.frontend_origin
    "Authentication:Enabled"                                   = tostring(var.authentication_enabled)
    "Authentication:Keycloak:Enabled"                          = tostring(var.authentication_enabled && var.enable_keycloak)
    "Authentication:Keycloak:Authority"                        = local.keycloak_authority
    "Authentication:Keycloak:Realm"                            = var.keycloak_realm
    "Authentication:Keycloak:Audience"                         = var.keycloak_api_audience
    "Authentication:Keycloak:SpaClientId"                      = var.keycloak_spa_client_id
    "Authentication:Keycloak:Scope"                            = "openid profile email"
    "Authentication:Keycloak:RequireHttpsMetadata"             = tostring(var.require_https_metadata)
    "Realtime:SignalRBaseUrl"                                  = local.signalr_url
    "Realtime:Cosmos:DatabaseName"                             = var.cosmos_database_name
    "Realtime:Cosmos:ProcessesContainerName"                   = "processes"
    "Realtime:Cosmos:TasksContainerName"                       = "processsteps"
    "Realtime:Cosmos:ProcessRealtimeSummariesContainerName"    = "processrealtimesummaries"
    "Realtime:Cosmos:ProcessNodeRealtimeContainerName"         = "processnoderealtime"
    "Realtime:Cosmos:LeaseContainerName"                       = "processstepprogressleases"
    "Realtime:UseHostedChangeFeed"                             = "true"
    "Realtime:InternalPublisher:BearerToken"                   = ""
    "PluginRuntime:HeartbeatIntervalSeconds"                   = "10"
    "Projections:SynchronousEngineUpdatesEnabled"              = tostring(!var.enable_projection_worker)
    "AzureStorage:BlobContainers:Plugins:ResourceName"         = "plugins"
    "AzureStorage:BlobContainers:Artifacts:ResourceName"       = "artifacts"
    "PluginsBlobContainerName"                                 = "plugins"
  }

  sql_connection_string = "Server=tcp:${module.sql.server_fqdn},1433;Initial Catalog=${module.sql.database_name};Persist Security Info=False;User ID=${var.sql_admin_username};Password=${local.sql_admin_password};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

  app_configuration_secret_values = {
    "ConnectionStrings:TasksDB"               = local.sql_connection_string
    "ConnectionStrings:enginedb"              = module.cosmos.primary_connection_string
    "ConnectionStrings:RealtimeCosmos"        = module.cosmos.primary_connection_string
    "ConnectionStrings:BlobConnection"        = module.storage.primary_connection_string
    "ConnectionStrings:process-step-queue"    = module.service_bus.primary_connection_string
    "ConnectionStrings:plugin-control-queue"  = module.service_bus.primary_connection_string
    "AzureWebJobsStorage"                     = module.storage.primary_connection_string
    "Realtime:InternalPublisher:SharedSecret" = random_password.internal_publisher_secret.result
  }

  key_vault_secret_names = {
    "ConnectionStrings:TasksDB"               = "connectionstrings-tasksdb"
    "ConnectionStrings:enginedb"              = "connectionstrings-enginedb"
    "ConnectionStrings:RealtimeCosmos"        = "connectionstrings-realtimecosmos"
    "ConnectionStrings:BlobConnection"        = "connectionstrings-blobconnection"
    "ConnectionStrings:process-step-queue"    = "connectionstrings-process-step-queue"
    "ConnectionStrings:plugin-control-queue"  = "connectionstrings-plugin-control-queue"
    "AzureWebJobsStorage"                     = "azurewebjobsstorage"
    "Realtime:InternalPublisher:SharedSecret" = "realtime-internal-publisher-shared-secret"
  }

  key_vault_secret_values = {
    for key, name in local.key_vault_secret_names :
    name => local.app_configuration_secret_values[key]
  }

  function_app_config_env_base = {
    "AzureAppConfiguration__Enabled"  = tostring(var.enable_app_configuration && var.functions_use_app_configuration)
    "AzureAppConfiguration__Endpoint" = local.app_config_endpoint
    "AzureAppConfiguration__Label"    = var.environment_name
  }

  progress_broadcaster_function_env = var.enable_app_configuration && var.functions_use_app_configuration ? {} : {
    "AzureCosmos__ConnectionName"                             = "enginedb"
    "AzureCosmos__DatabaseName"                               = var.cosmos_database_name
    "ConnectionStrings__TasksDB"                              = local.sql_connection_string
    "ConnectionStrings__enginedb"                             = module.cosmos.primary_connection_string
    "ConnectionStrings__RealtimeCosmos"                       = module.cosmos.primary_connection_string
    "RealtimeCosmos"                                          = module.cosmos.primary_connection_string
    "Realtime:Cosmos:DatabaseName"                            = var.cosmos_database_name
    "Realtime:Cosmos:TasksContainerName"                      = "processsteps"
    "Realtime:Cosmos:LeaseContainerName"                      = "processstepprogressleases"
    "Realtime__SignalRBaseUrl"                                = local.signalr_url
    "Realtime__Cosmos__DatabaseName"                          = var.cosmos_database_name
    "Realtime__Cosmos__ProcessesContainerName"                = "processes"
    "Realtime__Cosmos__TasksContainerName"                    = "processsteps"
    "Realtime__Cosmos__ProcessRealtimeSummariesContainerName" = "processrealtimesummaries"
    "Realtime__Cosmos__ProcessNodeRealtimeContainerName"      = "processnoderealtime"
    "Realtime__Cosmos__LeaseContainerName"                    = "processstepprogressleases"
    "Realtime__UseHostedChangeFeed"                           = "true"
    "Realtime__InternalPublisher__SharedSecret"               = random_password.internal_publisher_secret.result
  }

  plugin_detector_function_env = var.enable_app_configuration && var.functions_use_app_configuration ? {} : {
    "AzureCosmos__ConnectionName"                                 = "enginedb"
    "AzureCosmos__DatabaseName"                                   = var.cosmos_database_name
    "AzureServiceBus__Queues__PluginControlQueue__ConnectionName" = "plugin-control-queue"
    "AzureServiceBus__Queues__PluginControlQueue__Name"           = "pluginreloadqueue"
    "AzureStorage__BlobContainers__Plugins__ContainerName"        = "plugins"
    "ConnectionStrings__TasksDB"                                  = local.sql_connection_string
    "ConnectionStrings__enginedb"                                 = module.cosmos.primary_connection_string
    "ConnectionStrings__plugin-control-queue"                     = module.service_bus.primary_connection_string
    "PluginsBlobContainerName"                                    = "plugins"
  }
}

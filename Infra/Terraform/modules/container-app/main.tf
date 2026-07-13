resource "azurerm_container_app" "this" {
  name                         = var.name
  resource_group_name          = var.resource_group_name
  container_app_environment_id = var.container_app_environment_id
  revision_mode                = "Single"
  tags                         = var.tags

  lifecycle {
    ignore_changes = [
      registry,
      template[0].container[0].image,
      workload_profile_name
    ]
  }

  dynamic "identity" {
    for_each = length(var.identity_ids) == 0 ? [] : [1]
    content {
      type         = "UserAssigned"
      identity_ids = var.identity_ids
    }
  }

  dynamic "registry" {
    for_each = var.registry_server == null ? [] : [1]
    content {
      server   = var.registry_server
      identity = var.registry_identity_id
    }
  }

  dynamic "secret" {
    for_each = toset(keys(var.secret_values))
    content {
      name  = secret.value
      value = var.secret_values[secret.value]
    }
  }

  ingress {
    external_enabled = var.ingress_external_enabled
    target_port      = var.ingress_target_port

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    container {
      name   = var.container_name
      image  = var.image
      cpu    = var.cpu
      memory = var.memory

      dynamic "env" {
        for_each = var.env
        content {
          name        = env.value.name
          value       = try(env.value.value, null)
          secret_name = try(env.value.secret_name, null)
        }
      }
    }

    dynamic "custom_scale_rule" {
      for_each = var.custom_scale_rules
      content {
        name             = custom_scale_rule.value.name
        custom_rule_type = custom_scale_rule.value.custom_rule_type
        metadata         = custom_scale_rule.value.metadata

        dynamic "authentication" {
          for_each = custom_scale_rule.value.authentication
          content {
            secret_name       = authentication.value.secret_name
            trigger_parameter = authentication.value.trigger_parameter
          }
        }
      }
    }
  }
}

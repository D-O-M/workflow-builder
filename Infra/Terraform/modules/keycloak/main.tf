resource "azurerm_container_app" "this" {
  name                         = var.name
  resource_group_name          = var.resource_group_name
  container_app_environment_id = var.container_app_environment_id
  revision_mode                = "Single"
  tags                         = var.tags

  dynamic "identity" {
    for_each = length(var.identity_ids) == 0 ? [] : [1]
    content {
      type         = "UserAssigned"
      identity_ids = var.identity_ids
    }
  }

  secret {
    name  = "keycloak-admin-username"
    value = var.admin_username
  }

  secret {
    name  = "keycloak-admin-password"
    value = var.admin_password
  }

  ingress {
    external_enabled = true
    target_port      = 8080

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    container {
      name   = "keycloak"
      image  = var.image
      cpu    = var.cpu
      memory = var.memory
      args   = ["start-dev", "--http-enabled=true", "--hostname-strict=false"]

      env {
        name        = "KC_BOOTSTRAP_ADMIN_USERNAME"
        secret_name = "keycloak-admin-username"
      }

      env {
        name        = "KC_BOOTSTRAP_ADMIN_PASSWORD"
        secret_name = "keycloak-admin-password"
      }
    }
  }
}

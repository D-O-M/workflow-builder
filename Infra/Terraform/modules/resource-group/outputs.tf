output "name" {
  value = var.create ? azurerm_resource_group.this[0].name : data.azurerm_resource_group.this[0].name
}

output "location" {
  value = var.create ? azurerm_resource_group.this[0].location : data.azurerm_resource_group.this[0].location
}

output "id" {
  value = var.create ? azurerm_resource_group.this[0].id : data.azurerm_resource_group.this[0].id
}

resource "azurerm_servicebus_namespace" "this" {
  name                = var.namespace_name
  location            = var.location
  resource_group_name = var.resource_group_name
  sku                 = var.sku
  tags                = var.tags
}

resource "azurerm_servicebus_queue" "this" {
  for_each     = var.queues
  name         = each.value
  namespace_id = azurerm_servicebus_namespace.this.id
}

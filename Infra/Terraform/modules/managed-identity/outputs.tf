output "ids" {
  value = { for key, identity in azurerm_user_assigned_identity.this : key => identity.id }
}

output "principal_ids" {
  value = { for key, identity in azurerm_user_assigned_identity.this : key => identity.principal_id }
}

output "client_ids" {
  value = { for key, identity in azurerm_user_assigned_identity.this : key => identity.client_id }
}

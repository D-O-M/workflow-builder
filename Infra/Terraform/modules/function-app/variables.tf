variable "name" {
  type = string
}

variable "plan_name" {
  type = string
}

variable "resource_group_name" {
  type = string
}

variable "location" {
  type = string
}

variable "storage_account_name" {
  type = string
}

variable "storage_account_access_key" {
  type      = string
  sensitive = true
}

variable "identity_ids" {
  type    = list(string)
  default = []
}

variable "key_vault_reference_identity_id" {
  type    = string
  default = null
}

variable "app_settings" {
  type      = map(string)
  default   = {}
  sensitive = true
}

variable "dotnet_version" {
  type    = string
  default = "10.0"
}

variable "tags" {
  type    = map(string)
  default = {}
}

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

variable "storage_account_access_key" {
  type      = string
  sensitive = true
}

variable "storage_container_endpoint" {
  type        = string
  description = "Blob container endpoint used by Azure Functions Flex Consumption for deployment packages."
}

variable "identity_ids" {
  type    = list(string)
  default = []
}

variable "app_settings" {
  type      = map(string)
  default   = {}
  sensitive = true
}

variable "dotnet_version" {
  type    = string
  default = "10.0"

  validation {
    condition     = var.dotnet_version == "10.0"
    error_message = "WorkflowBuilder Function Apps currently target the Azure Functions dotnet-isolated 10.0 runtime."
  }
}

variable "maximum_instance_count" {
  type    = number
  default = 1
}

variable "instance_memory_in_mb" {
  type    = number
  default = 2048
}

variable "tags" {
  type    = map(string)
  default = {}
}

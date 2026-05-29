variable "name" {
  type = string
}

variable "resource_group_name" {
  type = string
}

variable "container_app_environment_id" {
  type = string
}

variable "image" {
  type = string
}

variable "container_name" {
  type = string
}

variable "cpu" {
  type = number
}

variable "memory" {
  type = string
}

variable "min_replicas" {
  type = number
}

variable "max_replicas" {
  type = number
}

variable "identity_ids" {
  type    = list(string)
  default = []
}

variable "registry_server" {
  type    = string
  default = null
}

variable "registry_identity_id" {
  type    = string
  default = null
}

variable "env" {
  type = list(object({
    name        = string
    value       = optional(string)
    secret_name = optional(string)
  }))
  default = []
}

variable "secret_values" {
  type    = map(string)
  default = {}
}

variable "custom_scale_rules" {
  type = list(object({
    name             = string
    custom_rule_type = string
    metadata         = map(string)
    authentication = optional(list(object({
      secret_name       = string
      trigger_parameter = string
    })), [])
  }))
  default = []
}

variable "tags" {
  type    = map(string)
  default = {}
}

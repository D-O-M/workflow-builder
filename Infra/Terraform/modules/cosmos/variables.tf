variable "account_name" {
  type = string
}

variable "database_name" {
  type = string
}

variable "resource_group_name" {
  type = string
}

variable "location" {
  type = string
}

variable "free_tier_enabled" {
  type = bool
}

variable "serverless_enabled" {
  type = bool
}

variable "containers" {
  type = map(object({
    partition_key = string
  }))
}

variable "tags" {
  type    = map(string)
  default = {}
}

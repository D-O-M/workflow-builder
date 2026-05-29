variable "account_name" {
  type = string
}

variable "resource_group_name" {
  type = string
}

variable "location" {
  type = string
}

variable "replication_type" {
  type = string
}

variable "containers" {
  type = list(string)
}

variable "tags" {
  type    = map(string)
  default = {}
}

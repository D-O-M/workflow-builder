variable "namespace_name" {
  type = string
}

variable "resource_group_name" {
  type = string
}

variable "location" {
  type = string
}

variable "sku" {
  type = string
}

variable "queues" {
  type = map(string)
}

variable "tags" {
  type    = map(string)
  default = {}
}

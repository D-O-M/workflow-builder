variable "name" {
  type = string
}

variable "resource_group_name" {
  type = string
}

variable "location" {
  type = string
}

variable "sku" {
  type    = string
  default = "standard"

  validation {
    condition     = contains(["free", "standard", "premium"], lower(var.sku))
    error_message = "sku must be free, standard, or premium."
  }
}

variable "tags" {
  type    = map(string)
  default = {}
}

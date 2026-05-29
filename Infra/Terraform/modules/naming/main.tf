locals {
  project_slug   = substr(replace(lower(var.project_name), "/[^a-z0-9-]/", "-"), 0, 24)
  environment    = substr(replace(lower(var.environment_name), "/[^a-z0-9-]/", "-"), 0, 12)
  suffix_part    = var.suffix == "" ? "" : "-${var.suffix}"
  prefix         = substr("${local.project_slug}-${local.environment}${local.suffix_part}", 0, 40)
  compact_prefix = substr(replace("${local.project_slug}${local.environment}${var.suffix}", "/[^a-z0-9]/", ""), 0, 18)
}

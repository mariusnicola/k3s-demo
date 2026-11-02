variable "kubeconfig" {
  type    = string
  default = "/etc/rancher/k3s/k3s.yaml"
}

# Holds <release>.json, the caps values setup writes before each apply.
variable "generated_dir" {
  type = string
}

# First-last, outside the DHCP pool. Setup skips the pool when it is empty.
variable "load_balancer_range" {
  type = string
}

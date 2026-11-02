terraform {
  required_version = ">= 1.17.1"

  required_providers {
    helm = {
      source  = "hashicorp/helm"
      version = "3.3.0"
    }
    kubernetes = {
      source  = "hashicorp/kubernetes"
      version = "3.4.0"
    }
  }
}

provider "helm" {
  kubernetes = {
    config_path = var.kubeconfig
  }
}

provider "kubernetes" {
  config_path = var.kubeconfig
}

# One release per chart, applied by setup one at a time in this order. Setup checks the rollout after each.

resource "helm_release" "metallb" {
  name             = "metallb"
  namespace        = "metallb-system"
  create_namespace = true
  repository       = "https://metallb.github.io/metallb"
  chart            = "metallb"
  version          = "0.16.1"
  # A LoadBalancer Service with no pool would never finish waiting.
  wait        = false
  max_history = 10
  values = [
    file("${path.module}/../cluster/metallb.yaml"),
    file("${var.generated_dir}/metallb.json"),
  ]
}

resource "helm_release" "ingress-nginx" {
  name             = "ingress-nginx"
  namespace        = "ingress-nginx"
  create_namespace = true
  repository       = "https://kubernetes.github.io/ingress-nginx"
  chart            = "ingress-nginx"
  version          = "4.15.1"
  wait             = false
  max_history      = 10
  values = [
    file("${path.module}/../cluster/ingress-nginx.yaml"),
    file("${var.generated_dir}/ingress-nginx.json"),
  ]
}

resource "helm_release" "prometheus" {
  name             = "prometheus"
  namespace        = "prometheus"
  create_namespace = true
  repository       = "https://prometheus-community.github.io/helm-charts"
  chart            = "prometheus"
  version          = "29.40.1"
  wait             = false
  max_history      = 10
  values = [
    file("${path.module}/../cluster/prometheus.yaml"),
    file("${var.generated_dir}/prometheus.json"),
  ]
}

resource "helm_release" "keda" {
  name             = "keda"
  namespace        = "keda"
  create_namespace = true
  repository       = "https://kedacore.github.io/charts"
  chart            = "keda"
  version          = "2.21.0"
  wait             = false
  max_history      = 10
  values = [
    file("${path.module}/../cluster/keda.yaml"),
    file("${var.generated_dir}/keda.json"),
  ]
}

resource "kubernetes_manifest" "pool" {
  depends_on = [helm_release.metallb]
  manifest = {
    apiVersion = "metallb.io/v1beta1"
    kind       = "IPAddressPool"
    metadata = {
      name      = "batchtracker"
      namespace = "metallb-system"
    }
    spec = {
      addresses = [var.load_balancer_range]
    }
  }
}

resource "kubernetes_manifest" "advertisement" {
  depends_on = [helm_release.metallb]
  manifest = {
    apiVersion = "metallb.io/v1beta1"
    kind       = "L2Advertisement"
    metadata = {
      name      = "batchtracker"
      namespace = "metallb-system"
    }
  }
}

# The namespace exists before the Secrets setup makes in it, and before the chart that reads them.
resource "kubernetes_namespace_v1" "batch-tracker" {
  metadata {
    name = "batch-tracker"
  }
}

resource "helm_release" "batch-tracker" {
  depends_on  = [kubernetes_namespace_v1.batch-tracker]
  name        = "batch-tracker"
  namespace   = "batch-tracker"
  chart       = "${path.module}/../app"
  wait        = true
  timeout     = 300
  max_history = 10
  values      = [file("${var.generated_dir}/batch-tracker.json")]
}

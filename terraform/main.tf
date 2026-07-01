terraform {
  required_providers {
    kubernetes = {
      source  = "hashicorp/kubernetes"
      version = "2.38.0"
    }
  }
}

provider "kubernetes" {
  config_path = "/work/kube.yaml"
}

resource "kubernetes_namespace" "iac" {
  metadata {
    name   = "terraform-demo"
    labels = { managedby = "terraform" }
  }
}

resource "kubernetes_deployment" "web" {
  metadata {
    name      = "tf-web"
    namespace = kubernetes_namespace.iac.metadata[0].name
    labels    = { app = "tf-web", managedby = "terraform" }
  }
  spec {
    replicas = 2
    selector { match_labels = { app = "tf-web" } }
    template {
      metadata { labels = { app = "tf-web" } }
      spec {
        container {
          name  = "web"
          image = "nginx:alpine"
          port { container_port = 80 }
        }
      }
    }
  }
}

output "namespace" {
  value = kubernetes_namespace.iac.metadata[0].name
}

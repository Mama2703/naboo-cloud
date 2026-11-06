terraform {
  required_version = ">= 1.6.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # État distant dans le compte de stockage créé par scripts/bootstrap-azure.sh
  # (paramètres passés par GitHub Actions avec -backend-config).
  backend "azurerm" {}
}

provider "azurerm" {
  features {}
  subscription_id = var.subscription_id
  # Les fournisseurs nécessaires sont enregistrés une fois par le script de démarrage.
  resource_provider_registrations = "none"
}

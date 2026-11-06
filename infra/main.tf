locals {
  nom     = "${var.prefixe}-${random_string.suffixe.result}"
  nom_api = "ca-${var.prefixe}-api"
  tags    = { projet = "naboo", gere_par = "terraform" }

  icloud_actif = var.icloud_url_calendrier != ""
  smtp_actif   = var.smtp_hote != ""

  # Les boucles ne dépendent que de valeurs non secrètes (exigence de Terraform) ;
  # les valeurs secrètes sont lues ensuite dans valeurs_secrets.
  secrets_actifs = concat(
    ["admin-mot-de-passe", "secret-session"],
    local.icloud_actif ? ["icloud-identifiant", "icloud-mot-de-passe-app"] : [],
    local.smtp_actif ? ["smtp-mot-de-passe"] : [],
  )

  valeurs_secrets = {
    "admin-mot-de-passe"      = var.admin_mot_de_passe
    "secret-session"          = random_password.session.result
    "icloud-identifiant"      = var.icloud_identifiant
    "icloud-mot-de-passe-app" = var.icloud_mot_de_passe
    "smtp-mot-de-passe"       = var.smtp_mot_de_passe
  }

  variables_secretes = { for k, v in {
    ADMIN_MOT_DE_PASSE      = "admin-mot-de-passe"
    SECRET_SESSION          = "secret-session"
    ICLOUD_IDENTIFIANT      = "icloud-identifiant"
    ICLOUD_MOT_DE_PASSE_APP = "icloud-mot-de-passe-app"
    SMTP_MOT_DE_PASSE       = "smtp-mot-de-passe"
  } : k => v if contains(local.secrets_actifs, v) }

  env = { for k, v in {
    ASPNETCORE_ENVIRONMENT = "Production"
    STOCKAGE               = "cosmos"
    COSMOS_ENDPOINT        = azurerm_cosmosdb_account.cosmos.endpoint
    COSMOS_BASE            = azurerm_cosmosdb_sql_database.db.name
    URL_API                = "https://${local.nom_api}.${azurerm_container_app_environment.env.default_domain}"
    CORS_ORIGINES          = "https://${azurerm_static_web_app.site.default_host_name}"
    EMAIL_PROPRIETAIRE     = var.email_proprietaire
    ICLOUD_URL_CALENDRIER  = var.icloud_url_calendrier
    SMTP_HOTE              = var.smtp_hote
    SMTP_PORT              = local.smtp_actif ? var.smtp_port : ""
    SMTP_UTILISATEUR       = var.smtp_utilisateur
    EMAIL_EXPEDITEUR       = var.smtp_utilisateur
  } : k => v if v != "" }
}

resource "random_string" "suffixe" {
  length  = 5
  upper   = false
  special = false
}

# Clé de signature des sessions de l'espace de gestion (jamais saisie à la main)
resource "random_password" "session" {
  length  = 48
  special = false
}

resource "azurerm_resource_group" "rg" {
  name     = "rg-${var.prefixe}"
  location = var.location
  tags     = local.tags
}

# ---------- Journaux (plafond journalier pour éviter toute facture surprise) ----------
resource "azurerm_log_analytics_workspace" "logs" {
  name                = "log-${local.nom}"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = 0.1
  tags                = local.tags
}

# ---------- Base de données : Cosmos DB, offre gratuite à vie ----------
resource "azurerm_cosmosdb_account" "cosmos" {
  name                = "cosmos-${local.nom}"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  offer_type          = "Standard"
  kind                = "GlobalDocumentDB"
  free_tier_enabled   = true

  # Pas de clé d'accès : seule l'identité managée de l'API peut lire/écrire
  local_authentication_disabled = true

  consistency_policy {
    consistency_level = "Session"
  }

  geo_location {
    location          = azurerm_resource_group.rg.location
    failover_priority = 0
  }

  # Garantit de rester dans les 1000 RU/s gratuites
  capacity {
    total_throughput_limit = 1000
  }

  tags = local.tags
}

resource "azurerm_cosmosdb_sql_database" "db" {
  name                = "naboo"
  resource_group_name = azurerm_resource_group.rg.name
  account_name        = azurerm_cosmosdb_account.cosmos.name
  throughput          = 400
}

resource "azurerm_cosmosdb_sql_container" "conteneurs" {
  for_each            = toset(["parametres", "rendezvous", "stats"])
  name                = each.key
  resource_group_name = azurerm_resource_group.rg.name
  account_name        = azurerm_cosmosdb_account.cosmos.name
  database_name       = azurerm_cosmosdb_sql_database.db.name
  partition_key_paths = ["/id"]
}

# ---------- API : Azure Container Apps (mise à l'échelle à zéro) ----------
resource "azurerm_container_app_environment" "env" {
  name                       = "cae-${local.nom}"
  location                   = azurerm_resource_group.rg.location
  resource_group_name        = azurerm_resource_group.rg.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.logs.id
  tags                       = local.tags
}

resource "azurerm_container_app" "api" {
  name                         = local.nom_api
  container_app_environment_id = azurerm_container_app_environment.env.id
  resource_group_name          = azurerm_resource_group.rg.name
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type = "SystemAssigned"
  }

  dynamic "secret" {
    for_each = toset(local.secrets_actifs)
    content {
      name  = secret.value
      value = local.valeurs_secrets[secret.value]
    }
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 0
    # Une seule réplique : garantit qu'un créneau ne peut pas être réservé deux fois en même temps
    max_replicas = 1

    container {
      name   = "api"
      image  = var.image
      cpu    = 0.25
      memory = "0.5Gi"

      dynamic "env" {
        for_each = local.env
        content {
          name  = env.key
          value = env.value
        }
      }

      dynamic "env" {
        for_each = local.variables_secretes
        content {
          name        = env.key
          secret_name = env.value
        }
      }

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/sante"
      }
    }
  }
}

# L'identité managée de l'API reçoit le rôle « Contributeur de données » sur Cosmos DB
resource "azurerm_cosmosdb_sql_role_assignment" "api" {
  resource_group_name = azurerm_resource_group.rg.name
  account_name        = azurerm_cosmosdb_account.cosmos.name
  role_definition_id  = "${azurerm_cosmosdb_account.cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
  principal_id        = azurerm_container_app.api.identity[0].principal_id
  scope               = azurerm_cosmosdb_account.cosmos.id
}

# ---------- Site public + espace de gestion : Static Web Apps (gratuit) ----------
resource "azurerm_static_web_app" "site" {
  name                = "swa-${local.nom}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = var.swa_location
  sku_tier            = "Free"
  sku_size            = "Free"
  tags                = local.tags
}

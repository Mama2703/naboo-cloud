variable "subscription_id" {
  description = "Identifiant de l'abonnement Azure (Azure for Students)."
  type        = string
}

variable "prefixe" {
  description = "Préfixe des noms de ressources."
  type        = string
  default     = "naboo"
}

variable "location" {
  description = "Région Azure des ressources (données hébergées en France)."
  type        = string
  default     = "francecentral"
}

variable "swa_location" {
  description = "Région de gestion du Static Web App (le contenu est servi par un CDN mondial)."
  type        = string
  default     = "westeurope"
}

variable "image" {
  description = "Image Docker de l'API (ghcr.io/...:<sha>), fournie par la CI."
  type        = string
}

variable "admin_mot_de_passe" {
  description = "Mot de passe initial de l'espace de gestion (10 caractères minimum)."
  type        = string
  sensitive   = true

  validation {
    condition     = length(var.admin_mot_de_passe) >= 10
    error_message = "Le mot de passe admin doit faire au moins 10 caractères."
  }
}

variable "email_proprietaire" {
  description = "Adresse qui reçoit les nouvelles demandes de rendez-vous."
  type        = string
}

variable "icloud_identifiant" {
  description = "Identifiant Apple (email) pour l'agenda iCloud. Vide = agenda désactivé."
  type        = string
  default     = ""
}

variable "icloud_mot_de_passe" {
  description = "Mot de passe pour app Apple."
  type        = string
  sensitive   = true
  default     = ""
}

variable "icloud_url_calendrier" {
  description = "URL CalDAV du calendrier iCloud (outil : scripts/trouver_calendrier_icloud.py)."
  type        = string
  default     = ""
}

variable "smtp_hote" {
  description = "Serveur SMTP (ex. smtp.gmail.com). Vide = emails désactivés (seulement journalisés)."
  type        = string
  default     = ""
}

variable "smtp_port" {
  type    = string
  default = "587"
}

variable "smtp_utilisateur" {
  description = "Compte SMTP (adresse email d'envoi)."
  type        = string
  default     = ""
}

variable "smtp_mot_de_passe" {
  description = "Mot de passe d'application du compte SMTP."
  type        = string
  sensitive   = true
  default     = ""
}

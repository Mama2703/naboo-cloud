output "api_url" {
  description = "Adresse publique de l'API."
  value       = "https://${azurerm_container_app.api.ingress[0].fqdn}"
}

output "site_url" {
  description = "Adresse du site public."
  value       = "https://${azurerm_static_web_app.site.default_host_name}"
}

output "swa_nom" {
  description = "Nom du Static Web App (pour le déploiement du site)."
  value       = azurerm_static_web_app.site.name
}

output "groupe_ressources" {
  value = azurerm_resource_group.rg.name
}

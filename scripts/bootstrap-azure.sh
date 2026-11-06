#!/usr/bin/env bash
# =============================================================================
# Préparation Azure — à lancer UNE SEULE FOIS dans Azure Cloud Shell (bash).
#
# Crée :
#   - le stockage de l'état Terraform (rg-naboo-tfstate),
#   - une identité managée que GitHub Actions utilise par OIDC (aucun mot de passe),
#   - ses droits (Contributeur sur l'abonnement, accès aux données de l'état Terraform),
# et enregistre les services Azure nécessaires.
#
# Usage : bash bootstrap-azure.sh [propriétaire/dépôt] [région]
# =============================================================================
set -euo pipefail

DEPOT="${1:-Mama2703/naboo-cloud}"
REGION="${2:-francecentral}"
RG="rg-naboo-tfstate"
IDENTITE="id-naboo-github"

ABONNEMENT=$(az account show --query id -o tsv)
LOCATAIRE=$(az account show --query tenantId -o tsv)
echo "▶ Abonnement : $(az account show --query name -o tsv)"

echo "▶ Enregistrement des services Azure (peut prendre quelques minutes)…"
for ns in Microsoft.App Microsoft.DocumentDB Microsoft.Web Microsoft.OperationalInsights Microsoft.Storage Microsoft.ManagedIdentity; do
  az provider register --namespace "$ns" --wait >/dev/null
done

echo "▶ Groupe de ressources $RG ($REGION)…"
az group create --name "$RG" --location "$REGION" --tags projet=naboo >/dev/null

STOCKAGE="stnabootf$(echo "$ABONNEMENT" | tr -d '-' | cut -c1-12)"
echo "▶ Compte de stockage de l'état Terraform : $STOCKAGE…"
az storage account create --name "$STOCKAGE" --resource-group "$RG" --location "$REGION" \
  --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 --allow-blob-public-access false >/dev/null
az storage container create --name tfstate --account-name "$STOCKAGE" --auth-mode key >/dev/null

echo "▶ Identité managée $IDENTITE pour GitHub Actions…"
az identity create --name "$IDENTITE" --resource-group "$RG" --location "$REGION" >/dev/null
CLIENT_ID=$(az identity show --name "$IDENTITE" --resource-group "$RG" --query clientId -o tsv)
PRINCIPAL_ID=$(az identity show --name "$IDENTITE" --resource-group "$RG" --query principalId -o tsv)

az identity federated-credential create --name github-main --identity-name "$IDENTITE" --resource-group "$RG" \
  --issuer "https://token.actions.githubusercontent.com" \
  --subject "repo:${DEPOT}:ref:refs/heads/main" \
  --audiences "api://AzureADTokenExchange" >/dev/null 2>&1 || echo "  (identifiant fédéré déjà présent)"

echo "▶ Droits de l'identité (attente de sa propagation)…"
sleep 30
az role assignment create --assignee-object-id "$PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
  --role Contributor --scope "/subscriptions/$ABONNEMENT" >/dev/null 2>&1 || echo "  (rôle Contributor déjà attribué)"
az role assignment create --assignee-object-id "$PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Data Contributor" \
  --scope "$(az storage account show --name "$STOCKAGE" --resource-group "$RG" --query id -o tsv)" >/dev/null 2>&1 \
  || echo "  (rôle Storage Blob Data Contributor déjà attribué)"

cat <<FIN

✅ Azure est prêt. Ajoute ces VARIABLES dans GitHub
   (dépôt ${DEPOT} → Settings → Secrets and variables → Actions → onglet Variables) :

   AZURE_CLIENT_ID          = ${CLIENT_ID}
   AZURE_TENANT_ID          = ${LOCATAIRE}
   AZURE_SUBSCRIPTION_ID    = ${ABONNEMENT}
   TFSTATE_STORAGE_ACCOUNT  = ${STOCKAGE}
   AZURE_REGION             = ${REGION}

(Ce ne sont pas des secrets : ce sont des identifiants publics.)
FIN

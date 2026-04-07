
# Azure Key Vault Authentication

This project now supports these authentication modes for Azure Key Vault:

1. Local development: Azure CLI login (`az login`)
2. Docker/server deployment: Managed Identity

Client secret authentication is no longer used.

## Required Environment Variable

Only the Key Vault URL is required:

```bash
KeyVault__KeyVaultUrl=https://your-vault-name.vault.azure.net/
```

## Local Development

```bash
az login
```

The application will authenticate with `AzureCliCredential`.

## Docker or Server Deployment

Assign a Managed Identity to the container host / VM / app service and grant it Key Vault secret permissions.

The application will authenticate with `ManagedIdentityCredential` when Azure CLI is not available.

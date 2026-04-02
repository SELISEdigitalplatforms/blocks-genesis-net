# Blocks Genesis Configuration Guide

This guide provides comprehensive instructions for configuring the Blocks Genesis framework for both on-premises and cloud deployments.

## Table of Contents

- [Overview](#overview)
- [Configuration Modes](#configuration-modes)
- [On-Premises Deployment](#on-premises-deployment)
- [Cloud Deployment (Azure)](#cloud-deployment-azure)
- [Common Configuration Variables](#common-configuration-variables)
- [Setting Environment Variables](#setting-environment-variables)

---

## Overview

Blocks Genesis supports multiple deployment scenarios through environment-specific configuration modes. The configuration system uses a hierarchical approach:

1. **Environment Variables** - Highest priority, overrides all other sources
2. **`.env` File** - Local override file for development (optional)
3. **`appsettings.json`** - Environment-specific defaults
4. **`appsettings.{Environment}.json`** - Environment overrides

The framework uses the `ASPNETCORE_ENVIRONMENT` variable to determine which configuration set to load.

---

## Configuration Modes

### SecretMode

The application supports three configuration modes, controlled by the `SecretMode` parameter:

- **`OnPrem`** - On-premises or self-hosted deployments using environment variables and local stores
- **`KeyVault`** - Azure Key Vault integration for managed secrets (cloud deployments)
- **`SecretsManager`** - AWS Secrets Manager integration (future support)

---

## On-Premises Deployment

### Required Environment Variables

For on-premises deployments, set the following environment variables using the `BlocksSecret__` prefix:

#### Core Infrastructure

| Variable | Purpose | Example |
|----------|---------|---------|
| `BlocksSecret__CacheConnectionString` | Redis cache connection string | `localhost:6379` |
| `BlocksSecret__DatabaseConnectionString` | MongoDB connection string | `mongodb://localhost:27017` |
| `BlocksSecret__RootDatabaseName` | Primary database name | `blocks_genesis` |
| `BlocksSecret__MessageConnectionString` | Message broker connection | `amqp://localhost:5672` |

#### Observability & Telemetry

| Variable | Purpose | Example |
|----------|---------|---------|
| `BlocksSecret__LogConnectionString` | MongoDB connection for logs | `mongodb://localhost:27017` |
| `BlocksSecret__LogDatabaseName` | Database name for log storage | `logs` |
| `BlocksSecret__TraceConnectionString` | MongoDB connection for traces | `mongodb://localhost:27017` |
| `BlocksSecret__TraceDatabaseName` | Database name for trace storage | `traces` |
| `BlocksSecret__MetricConnectionString` | MongoDB connection for metrics | `mongodb://localhost:27017` |
| `BlocksSecret__MetricDatabaseName` | Database name for metric storage | `metrics` |

#### LMT (Logging, Metrics, Tracing) Configuration

| Variable | Purpose | Example |
|----------|---------|---------|
| `BlocksSecret__LmtMessageConnectionString` | LMT transport connection | `amqp://localhost:5672` |
| `BlocksSecret__LmtBlobStorageConnectionString` | LMT blob storage connection | `DefaultEndpointsProtocol=https;...` |

#### Application Settings

| Variable | Purpose | Example |
|----------|---------|---------|
| `BlocksSecret__ServiceName` | Service identifier for logs/traces | `my-api-service` |
| `BlocksSecret__EnableHsts` | Enable HSTS header enforcement | `true` or `false` |

### Setting Environment Variables on Windows

#### Method 1: Command Prompt (Current Session Only)

```cmd
set BlocksSecret__CacheConnectionString=localhost:6379
set BlocksSecret__DatabaseConnectionString=mongodb://localhost:27017
set BlocksSecret__RootDatabaseName=blocks_genesis
set ASPNETCORE_ENVIRONMENT=Development
```

#### Method 2: PowerShell (Current Session Only)

```powershell
$env:BlocksSecret__CacheConnectionString = "localhost:6379"
$env:BlocksSecret__DatabaseConnectionString = "mongodb://localhost:27017"
$env:BlocksSecret__RootDatabaseName = "blocks_genesis"
$env:ASPNETCORE_ENVIRONMENT = "Development"
```

#### Method 3: System Properties (Persistent)

1. Open **System Properties** → **Environment Variables** (or search "environment variables" in Start Menu)
2. Click **"New..."** under User variables or System variables
3. Enter variable name (e.g., `BlocksSecret__CacheConnectionString`) and value
4. Click **OK** and restart your application for changes to take effect

### Setting Environment Variables on Linux/macOS

#### Method 1: Bash/Zsh Shell (Current Session Only)

```bash
export BlocksSecret__CacheConnectionString="localhost:6379"
export BlocksSecret__DatabaseConnectionString="mongodb://localhost:27017"
export BlocksSecret__RootDatabaseName="blocks_genesis"
export ASPNETCORE_ENVIRONMENT="Development"
```

#### Method 2: Persistent Shell Configuration (`~/.bashrc` or `~/.zshrc`)

Add exports to your shell profile file:

```bash
# ~/.bashrc or ~/.zshrc
echo 'export BlocksSecret__CacheConnectionString="localhost:6379"' >> ~/.bashrc
echo 'export BlocksSecret__DatabaseConnectionString="mongodb://localhost:27017"' >> ~/.bashrc
echo 'export BlocksSecret__RootDatabaseName="blocks_genesis"' >> ~/.bashrc
echo 'export ASPNETCORE_ENVIRONMENT="Development"' >> ~/.bashrc
source ~/.bashrc
```

Or edit the file directly:

```bash
nano ~/.bashrc  # or vim, code, etc.
# Add these lines at the end:
export BlocksSecret__CacheConnectionString="localhost:6379"
export BlocksSecret__DatabaseConnectionString="mongodb://localhost:27017"
export BlocksSecret__RootDatabaseName="blocks_genesis"
export ASPNETCORE_ENVIRONMENT="Development"
```

#### Method 3: Systemd Service Environment (Persistent, Production)

For applications running as Systemd services, create an environment file:

```bash
# /etc/environment.d/blocks-genesis.conf (or create a new file)
BlocksSecret__CacheConnectionString="localhost:6379"
BlocksSecret__DatabaseConnectionString="mongodb://localhost:27017"
BlocksSecret__RootDatabaseName="blocks_genesis"
ASPNETCORE_ENVIRONMENT="Production"
```

Then reference it in your Systemd unit file:

```ini
# /etc/systemd/system/my-blocks-api.service
[Unit]
Description=Blocks Genesis API Service
After=network.target

[Service]
Type=notify
User=www-data
WorkingDirectory=/opt/my-blocks-api

# Load environment variables
EnvironmentFile=/etc/environment.d/blocks-genesis.conf

ExecStart=/path/to/my-blocks-api

Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
```

Reload and restart the service:

```bash
sudo systemctl daemon-reload
sudo systemctl restart my-blocks-api
```

#### Docker Environment Variables

When running in Docker, pass environment variables via `docker run`:

```bash
docker run -d \
  -e BlocksSecret__CacheConnectionString="redis:6379" \
  -e BlocksSecret__DatabaseConnectionString="mongodb://mongo:27017" \
  -e BlocksSecret__RootDatabaseName="blocks_genesis" \
  -e ASPNETCORE_ENVIRONMENT="Production" \
  my-blocks-api
```

Or define in a `docker-compose.yml`:

```yaml
services:
  api:
    image: my-blocks-api:latest
    environment:
      - BlocksSecret__CacheConnectionString=redis:6379
      - BlocksSecret__DatabaseConnectionString=mongodb://mongo:27017
      - BlocksSecret__RootDatabaseName=blocks_genesis
      - ASPNETCORE_ENVIRONMENT=Production
    depends_on:
      - redis
      - mongo
```

---

## Cloud Deployment (Azure)

### Azure Key Vault Integration

For cloud deployments using Azure Key Vault, the application uses the `KeyVault` configuration mode with Azure managed identity or service principal authentication.

#### Required Key Vault Variables

| Variable | Purpose | Example |
|----------|---------|---------|
| `KeyVault__KeyVaultUrl` | Azure Key Vault URL | `https://my-keyvault.vault.azure.net/` |
| `KeyVault__TenantId` | Azure AD Tenant ID | `{tenant-guid}` |
| `KeyVault__ClientId` | Service Principal Client ID | `{client-guid}` |
| `KeyVault__ClientSecret` | Service Principal Client Secret | `{secret-value}` |

#### Setting Azure Key Vault Variables

##### Using Azure CLI

```bash
az keyvault secret set --vault-name my-keyvault \
  --name "KeyVaultUrl" \
  --value "https://my-keyvault.vault.azure.net/"

az keyvault secret set --vault-name my-keyvault \
  --name "TenantId" \
  --value "{tenant-guid}"
```

##### Using Azure Portal

1. Go to **Azure Key Vault** → Your vault
2. Select **Secrets** → **Generate/Import**
3. Enter key name (e.g., `KeyVaultUrl`) and value
4. Click **Create**

##### Using Environment Variables (Azure Container Instances)

When deploying to Azure Container Instances or App Service, set variables in the Azure portal under **Configuration** → **Application settings**:

- `KeyVault__KeyVaultUrl`: `https://my-keyvault.vault.azure.net/`
- `KeyVault__TenantId`: `{tenant-guid}`
- `KeyVault__ClientId`: `{client-guid}`
- `KeyVault__ClientSecret`: `{secret-value}`

#### Using Managed Identity (Recommended)

For enhanced security, use Azure Managed Identity instead of service principal credentials:

```csharp
// In your configuration setup
builder.Services.AddAzureClients(clientBuilder =>
{
    clientBuilder.AddSecretClient(new Uri(keyVaultUrl))
        .WithCredential(new DefaultAzureCredential());
});
```

---

## Common Configuration Variables

### Message Broker Configuration

Choose **one** of the following:

#### Azure Service Bus

```env
BlocksSecret__MessageConnectionString=Endpoint=sb://my-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey={key}
```

#### RabbitMQ

```env
BlocksSecret__MessageConnectionString=amqp://username:password@localhost:5672/
```

**Important**: Configure only one message broker. Configuring both will result in a runtime error.

### Database Configuration

Blocks Genesis uses MongoDB for primary data storage:

```env
BlocksSecret__DatabaseConnectionString=mongodb://user:password@host:27017/
BlocksSecret__RootDatabaseName=blocks_genesis
```

### Caching Configuration

Redis is used for distributed caching:

```env
BlocksSecret__CacheConnectionString=localhost:6379,ssl=false
```

### Logging & Observability

All observability integrations point to MongoDB collections:

```env
BlocksSecret__LogConnectionString=mongodb://user:password@host:27017/
BlocksSecret__LogDatabaseName=logs

BlocksSecret__TraceConnectionString=mongodb://user:password@host:27017/
BlocksSecret__TraceDatabaseName=traces

BlocksSecret__MetricConnectionString=mongodb://user:password@host:27017/
BlocksSecret__MetricDatabaseName=metrics
```

---

## Setting Environment Variables

### Validation & Verification

After setting environment variables, verify they are correctly loaded:

#### .NET Application Startup

The application will log configuration load status:

```
[Information] Blocks project key configured: {BlocksProjectKey}
[Information] Loaded environment variables from .env file
```

#### Manual Verification

##### Windows PowerShell

```powershell
$env:BlocksSecret__CacheConnectionString  # Display variable value
Get-ChildItem env: | Where-Object { $_.Name -like "*BlocksSecret*" }  # List all
```

##### Linux/macOS

```bash
echo $BlocksSecret__CacheConnectionString  # Display variable value
env | grep BlocksSecret  # List all
```

### Best Practices

1. **Use Environment-Specific Files**: Maintain separate `.env` files for development, staging, and production
2. **Secure Secrets**: Never commit secrets to version control; use `.gitignore` for `.env` files
3. **Use Managed Secrets**: For production, use Azure Key Vault or equivalent secret management service
4. **Validate on Startup**: The application validates required configuration on startup and will fail with clear error messages if variables are missing
5. **Document Custom Variables**: If adding custom configuration, document all required variables in your service's README

---

## Troubleshooting

### Missing Configuration Variables

If the application fails to start with configuration errors:

1. Verify all required environment variables are set
2. Check the application logs for specific variable names that are missing
3. Ensure proper prefixes (`BlocksSecret__` for on-premises, `KeyVault__` for cloud)
4. Verify values are correctly escaped if containing special characters

### Connection String Issues

- **MongoDB**: Ensure connection string includes authentication if required: `mongodb://user:password@host:port/`
- **Redis**: Verify port (default `6379`) and SSL settings if applicable
- **RabbitMQ**: Confirm AMQP protocol and credentials: `amqp://user:password@host:port/`
- **Service Bus**: Validate Endpoint and SharedAccessKey format

### Azure Key Vault Access Denied

- Verify service principal has "Get" and "List" permissions on secrets
- Ensure Key Vault firewall rules allow your application's network
- Confirm TenantId, ClientId, and ClientSecret are correct

---

## Related Documentation

- [Blocks Genesis README](../../README.md) - Project overview and architecture
- [LMT Client Configuration](../Blocks.LMT.Client/readme.md) - Logging, metrics, and tracing setup
- [Security Guidelines](../../SECURITY.md) - Security best practices

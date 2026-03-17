
# Example Environment Variables (OS Agnostic)

To populate the `KeyVault` class properties using environment variables, set them as follows in your operating system:

```
KeyVault__KeyVaultUrl=your_cache_connection_string_from_env  
KeyVault__TenantId=your_message_queue_connection_string_from_env 
KeyVault__ClientId=your_message_queue_connection_string_from_env  
KeyVault__ClientSecret=your_logging_connection_string_from_env  
```


## Different OS Environment Variable Examples (How to Set Them)

### 1. **Windows**

#### Using Command Prompt (for the current session):
```
set KeyVault__KeyVaultUrl = "KeyVaultUrl"
set KeyVault__ClientSecret = "ClientSecret" 
```

#### Using PowerShell (for the current session):
```
$env:KeyVault__KeyVaultUrl = "KeyVaultUrl"  
$env:KeyVault__ClientSecret = "ClientSecret"  
```

#### Using System Properties (persistent):
- Search for "environment variables" in the Start Menu.
- Click on **"Edit the system environment variables"**.
- In the "System Properties" dialog, click the **"Environment Variables..."** button.
- You can set **user variables** (for your account) or **system variables** (for all users).
- Click **"New..."** and enter the variable name (e.g., `KeyVault__KeyVaultUrl`) and its value.

---

### 2. **Linux (Bash/Zsh)**

#### Using the `export` command (for the current session):
```
export KeyVault__KeyVaultUrl = "KeyVaultUrl" 
export KeyVault__ClientSecret = "ClientSecret"  
```

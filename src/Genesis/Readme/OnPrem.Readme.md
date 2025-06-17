
# Example Environment Variables (OS Agnostic)

To populate the `BlocksSecret` class properties using environment variables, set them as follows in your operating system:

```
BlocksSecret__CacheConnectionString=your_cache_connection_string_from_env  
BlocksSecret__MessageConnectionString=your_message_queue_connection_string_from_env  
BlocksSecret__LogConnectionString=your_logging_connection_string_from_env  
BlocksSecret__MetricConnectionString=your_metrics_connection_string_from_env  
BlocksSecret__TraceConnectionString=your_tracing_connection_string_from_env  
BlocksSecret__LogDatabaseName=your_log_database_from_env  
BlocksSecret__MetricDatabaseName=your_metric_database_from_env  
BlocksSecret__TraceDatabaseName=your_trace_database_from_env 
BlocksSecret__DatabaseConnectionString=your_main_database_connection_string_from_env  
BlocksSecret__RootDatabaseName=your_root_database_from_env  
BlocksSecret__EnableHsts=true
```


## Different OS Environment Variable Examples (How to Set Them)

### 1. **Windows**

#### Using Command Prompt (for the current session):
```
set BlocksSecret__CacheConnectionString=your_cache_string  
set BlocksSecret__ServiceName=my_app  
```

#### Using PowerShell (for the current session):
```
$env:BlocksSecret__CacheConnectionString = "your_cache_string"  
$env:BlocksSecret__ServiceName = "my_app"  
```

#### Using System Properties (persistent):
- Search for "environment variables" in the Start Menu.
- Click on **"Edit the system environment variables"**.
- In the "System Properties" dialog, click the **"Environment Variables..."** button.
- You can set **user variables** (for your account) or **system variables** (for all users).
- Click **"New..."** and enter the variable name (e.g., `BlocksSecret__CacheConnectionString`) and its value.

---

### 2. **Linux (Bash/Zsh)**

#### Using the `export` command (for the current session):
```
export BlocksSecret__CacheConnectionString="your_cache_string"  
export BlocksSecret__ServiceName="my_app"  
```

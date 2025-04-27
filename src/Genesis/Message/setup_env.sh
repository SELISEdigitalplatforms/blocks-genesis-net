#!/bin/bash

# Direct environment variable setup script
# This script detects the operating system and sets environment variables directly

# Detect operating system
if [[ "$OSTYPE" == "linux-gnu"* ]]; then
    OS_TYPE="Linux"
elif [[ "$OSTYPE" == "darwin"* ]]; then
    OS_TYPE="macOS"
elif [[ "$OSTYPE" == "msys" || "$OSTYPE" == "cygwin" || "$OSTYPE" == "win32" ]]; then
    OS_TYPE="Windows"
else
    echo "Unknown operating system: $OSTYPE"
    exit 1
fi

echo "Detected operating system: $OS_TYPE"
echo "Setting up environment variables..."

# Function to set environment variables based on OS
set_env_variable() {
    local var_name=$1
    local var_value=$2
    
    if [[ "$OS_TYPE" == "Windows" ]]; then
        # For Windows
        export $var_name="$var_value"
        if command -v setx &> /dev/null; then
            setx $var_name "$var_value" > /dev/null
            echo "Set $var_name using setx"
        else
            echo "Warning: setx command not available. $var_name set for current session only."
        fi
    elif [[ "$OS_TYPE" == "Linux" || "$OS_TYPE" == "macOS" ]]; then
        # For Linux/macOS
        export $var_name="$var_value"
        echo "Set $var_name for current session"
    fi
}

# Set all the environment variables
set_env_variable "BlocksSecret__CacheConnectionString" "your_cache_connection_string_from_env"
set_env_variable "BlocksSecret__MessageConnectionString" "your_message_queue_connection_string_from_env"
set_env_variable "BlocksSecret__LogConnectionString" "your_logging_connection_string_from_env"
set_env_variable "BlocksSecret__MetricConnectionString" "your_metrics_connection_string_from_env"
set_env_variable "BlocksSecret__TraceConnectionString" "your_tracing_connection_string_from_env"
set_env_variable "BlocksSecret__LogDatabaseName" "your_log_database_from_env"
set_env_variable "BlocksSecret__MetricDatabaseName" "your_metric_database_from_env"
set_env_variable "BlocksSecret__TraceDatabaseName" "your_trace_database_from_env"
set_env_variable "BlocksSecret__ServiceName" "your_service_name_from_env"
set_env_variable "BlocksSecret__DatabaseConnectionString" "your_main_database_connection_string_from_env"
set_env_variable "BlocksSecret__RootDatabaseName" "your_root_database_from_env"
set_env_variable "BlocksSecret__EnableHsts" "true"
set_env_variable "BlocksSecret__SshHost" "your_ssh_host_from_env"
set_env_variable "BlocksSecret__SshUsername" "your_ssh_username_from_env"
set_env_variable "BlocksSecret__SshPassword" "your_ssh_password_from_env"
set_env_variable "BlocksSecret__SshNginxTemplate" "/path/to/nginx/template_from_env"

echo "Environment variables have been set for the current session."

if [[ "$OS_TYPE" == "Linux" || "$OS_TYPE" == "macOS" ]]; then
    echo ""
    echo "IMPORTANT: For these variables to persist, this script must be sourced, not executed."
    echo "Run it with: source setup_env.sh"
    echo ""
    echo "If you're seeing this message, you may have run the script with ./setup_env.sh"
    echo "In that case, the variables will only be available to this script itself."
fi

if [[ "$OS_TYPE" == "Windows" ]]; then
    echo ""
    echo "IMPORTANT: On Windows, this script must be run in a bash environment (Git Bash, WSL, etc.)"
    echo "For permanent environment variables on Windows, the setx command was used if available."
    echo "You may need to restart your command prompt for the changes to take effect."
fi
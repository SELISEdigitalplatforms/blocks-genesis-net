# Blocks Genesis Service

SELISE `<blocks />` Genesis is a .NET service foundation for building APIs and workers with built-in configuration, middleware, messaging, cache, observability, and multi-tenant utilities.

Naming convention: `Blocks <Service_Name> Service`

## Overview

This repository provides a multi-project .NET solution containing a reusable framework package, sample API/worker services, tests, and an LMT client package for both .NET and Node.js workloads.

## Table of Content

- [Overview](#overview)
- [Table of Content](#table-of-content)
- [Feature](#feature)
- [Technology Stack](#technology-stack)
- [Project Structure](#project-structure)
- [Controller / Endpoint](#controller--endpoint)
- [Prerequisites](#prerequisites)
- [Getting Started](#getting-started)
- [Installation](#installation)
- [Environment Variables](#environment-variables)

## Feature

- Reusable application bootstrap and middleware pipeline for API and worker services
- Built-in support for secrets, logging, tracing, metrics, and health integrations
- Message broker integration patterns for Azure Service Bus and RabbitMQ consumers
- Multi-tenant request context handling through `BlocksContext`
- Sample HTTP and gRPC services demonstrating Genesis integration
- Shared LMT client libraries for .NET and Node.js
- Unit tests and driver utilities for validation and integration scenarios

## Technology Stack

- .NET 9
- ASP.NET Core Web API
- ASP.NET Core gRPC
- Worker Service
- MongoDB Driver
- Azure Service Bus / RabbitMQ integration
- xUnit + Moq + FluentAssertions
- TypeScript (Node package)

## Project Structure

```text
.
├── src
│   ├── Genesis                # Core framework package
│   ├── Blocks.LMT.Client      # Shared .NET LMT client
│   ├── Api1                   # Sample HTTP API service
│   ├── Apis                   # Sample API + gRPC service
│   ├── Workers                # Worker sample (Azure Service Bus)
│   ├── WorkerTwo              # Worker sample (RabbitMQ)
│   ├── TestDriver             # Helper driver utilities
│   └── XUnitTest              # Unit test project
├── node                       # @seliseblocks/lmt-client package
├── Apis.Dockerfile            # API image definition
└── Workers.Dockerfile         # Worker image definition
```

## Controller / Endpoint

Routing pattern (HTTP): `/api/[controller]/[action-or-route]`

### S1Controller (Api1)

Base route: `/api/s1`

- `GET /api/s1/process` - Processes a request with tenant context, triggers messaging and downstream API/gRPC calls, and returns aggregated data.
- `GET /api/s1/cert` - Returns the current request context for protected certificate-authenticated access checks.

### S2Controller (Apis)

Base route: `/api/s2`

- `GET /api/s2/process` - Handles a sample request, publishes a queue message, and returns the current `BlocksContext`.
- `GET /api/s2/process_1` - Returns the current `BlocksContext` for a lightweight context-validation endpoint.

### GreeterService (gRPC, Apis)

Service route: `/Greeter/SayHello`

- `RPC Greeter/SayHello` - Returns a serialized `BlocksContext` payload to verify request context propagation over gRPC.

Note: Endpoints that use `Authorize` or `ProtectedEndPoint` require valid authentication and expected request headers/context.

## Prerequisites

- .NET SDK 9.0 or later
- Node.js 16 or later (for `node/` package)
- Access to required runtime dependencies for local integration (for example MongoDB, Redis, message broker)
- Required secret/configuration provider values (environment variables or vault)
- Docker Desktop (optional, for containerized builds)

## Getting Started

1. Clone the repository.
2. Move to the solution root.
3. Restore and build the solution.
4. Configure environment settings and secrets.
5. Run API and worker services.

Basic build commands:

```sh
dotnet restore src/blocks-genesis-net.sln
dotnet build src/blocks-genesis-net.sln
```

Run sample services locally in separate terminals:

```sh
dotnet run --project src/Api1/ApiOne.csproj
dotnet run --project src/Apis/ApiTwo.csproj
dotnet run --project src/Workers/WorkerOne.csproj
dotnet run --project src/WorkerTwo/WorkerTwo.csproj
```

Run tests:

```sh
dotnet test src/XUnitTest/XUnitTest.csproj
```

Build Node package:

```sh
cd node
npm install
npm run build
```

## Installation

### 1. Clone the Repository

```sh
git clone https://github.com/SELISEdigitalplatforms/blocks-genesis-net.git
cd blocks-genesis-net
```

### 2. Restore Dependencies

```sh
dotnet restore src/blocks-genesis-net.sln
```

### 3. Configure Environment Variables and Secrets

Set required `BlocksSecret__*` values for your target environment.

Quick local setup helper:

```sh
cd src/Genesis
source setup_env.sh
```

### 4. Build the Solution

```sh
dotnet build src/blocks-genesis-net.sln
```

### 5. Optional Docker Builds

From the repository root:

```sh
docker build -f Apis.Dockerfile -t blocks-genesis-api --build-arg git_branch=dev .
docker build -f Workers.Dockerfile -t blocks-genesis-worker --build-arg git_branch=dev .
```

## Environment Variables

### Essentials

- `BlocksSecret__CacheConnectionString`
- `BlocksSecret__MessageConnectionString`
- `BlocksSecret__LogConnectionString`
- `BlocksSecret__MetricConnectionString`
- `BlocksSecret__TraceConnectionString`
- `BlocksSecret__LogDatabaseName`
- `BlocksSecret__MetricDatabaseName`
- `BlocksSecret__TraceDatabaseName`
- `BlocksSecret__DatabaseConnectionString`
- `BlocksSecret__RootDatabaseName`
- `BlocksSecret__EnableHsts`

### KeyVault

If you want to access these environment variables from KeyVault then add the following variables instead:

- `CacheConnectionString`
- `MessageConnectionString`
- `LogConnectionString`
- `MetricConnectionString`
- `TraceConnectionString`
- `LogDatabaseName`
- `MetricDatabaseName`
- `TraceDatabaseName`
- `DatabaseConnectionString`
- `RootDatabaseName`
- `EnableHsts`
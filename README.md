# Blocks Genesis

> A comprehensive, production-ready microservices framework for building scalable, observable, and resilient distributed systems with built-in logging, tracing, caching, messaging, and security.


![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)
![Docker](https://img.shields.io/badge/docker-ready-2496ED?logo=docker&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green.svg)
---

## Table of Contents

- [Overview](#overview)
- [Features](#features)
- [Architecture Overview](#architecture-overview)
- [Controllers / Endpoints](#controllers--endpoints)
- [Tech Stack](#tech-stack)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Configuration — Environment Variables](#configuration--environment-variables)
- [Running the Project Locally](#running-the-project-locally)
- [Usage](#usage)
- [Contributing](#contributing)
- [License](#license)
- [Maintainers](#maintainers)

---

## Overview

Blocks Genesis is an enterprise-grade microservices framework built on .NET 9.0 that simplifies the development of distributed systems. It provides a unified foundation for multi-service architectures with built-in support for structured logging, distributed tracing, caching, asynchronous messaging, multi-tenancy, and API authentication. The framework abstracts away infrastructure concerns, allowing teams to focus on business logic while maintaining observability, resilience, and security across all services.

This project demonstrates a reference implementation with multiple API services, background workers, gRPC communication, event-driven messaging, and a Node.js observability client.

**Target Users:** Enterprise teams building microservices, SaaS platforms, event-driven architectures, and require centralized observability.

### Key Use Cases

- **Real-time Event Processing** — Process domain events across multiple services with guaranteed delivery and multi-tenant isolation
- **Microservice Communication** — API-to-API HTTP calls, asynchronous messaging, and gRPC for polyglot service interactions
- **Centralized Observability** — Correlate logs and traces across services using automatic tenant context propagation
- **Multi-Tenant SaaS** — Build isolated, secure tenant experiences with automatic context routing
- **Background Job Processing** — Execute long-running tasks with service bus integration and automatic retry logic

---

## Features

- **Event-Driven Architecture** — Publish and subscribe to domain events via Azure Service Bus or RabbitMQ with built-in batching and retry logic
- **Distributed Tracing** — OpenTelemetry integration with automatic context propagation across service boundaries
- **Structured Logging** — Serilog-based centralized logging to MongoDB with tenant and request correlation
- **High-Speed Caching** — Redis-backed cache for tokens, JWKS, and application data with automatic expiration
- **JWT Authentication** — JWT Bearer token validation with support for third-party token issuers
- **Multi-Tenant Isolation** — Automatic tenant context extraction and propagation across service calls
- **gRPC Support** — Native gRPC service definitions with protobuf and automatic middleware integration
- **Metrics Collection** — Prometheus-style metrics backed by MongoDB with OpenTelemetry integration
- **Azure Key Vault Integration** — Seamless secret rotation from Azure Key Vault in production environments
- **Auto-Scaling Worker Services** — Background workers with configurable concurrency and resilience patterns
- **Health Checks** — Built-in health check endpoints for orchestration platforms
- **Swagger/OpenAPI Documentation** — Auto-generated interactive API documentation with security schemes

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│                            Blocks Genesis Microservices                                 │
├─────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                         │
│  ┌──────────────────────────┐         ┌──────────────────────────┐                     │
│  │   ApiOne (Service-API-   │         │   ApiTwo (Service-API-   │                     │
│  │   Test_One)              │         │   Test_Two)              │                     │
│  │                          │         │                          │                     │
│  │  • S1 REST Controller    │         │  • S2 REST Controller    │                     │
│  │  • Process Requests      │         │  • Greeter gRPC Service  │                     │
│  │  • HTTP Port: 4000/4001  │         │  • HTTP Port: 5131       │                     │
│  │  • Consul: Azure SB      │         │  • Consul: RabbitMQ      │                     │
│  └──────────┬───────────────┘         │                          │                     │
│             │                         └────────────┬─────────────┘                     │
│             │                                      │                                   │
│             └──────────────┬───────────────────────┘                                   │
│                            │                                                           │
│              ┌─────────────▼─────────────┐                                             │
│              │  Azure Service Bus /      │                                             │
│              │  RabbitMQ Message Broker  │                                             │
│              │                           │                                             │
│              │  • Topics: demo_topic,    │                                             │
│              │    demo_topic_1           │                                             │
│              │  • Queues: demo_queue,    │                                             │
│              │    test_from_cloud_queue  │                                             │
│              └─────────────┬─────────────┘                                             │
│                            │                                                           │
│  ┌───────────────────────┐ │  ┌───────────────────────┐                               │
│  │  WorkerOne            │ │  │  WorkerTwo            │                               │
│  │  (Service-Worker-     │ │  │  (Service-Worker-     │                               │
│  │  Test_One)            │ │  │  Test_Two)            │                               │
│  │                       │ │  │                       │                               │
│  │  • W1Consumer         │◄┼──┤  • W1Consumer         │                               │
│  │  • W2Consumer         │ │  │  • W2Consumer         │                               │
│  │  • Event Handlers     │ │  │  • RabbitMQ Consumers │                               │
│  └───────────┬───────────┘ │  └───────────┬───────────┘                               │
│              │              │             │                                            │
│              └──────────────┼─────────────┘                                            │
│                             │                                                          │
│     ┌───────────────────────┼───────────────────────┐                                  │
│     │                       │                       │                                  │
│     ▼                       ▼                       ▼                                  │
│  ┌──────────────┐    ┌─────────────────┐    ┌──────────────┐                          │
│  │   MongoDB    │    │     Redis       │    │  Azure Key   │                          │
│  │              │    │                 │    │  Vault       │                          │
│  │  • Logs      │    │  • JWT Cache    │    │              │                          │
│  │  • Metrics   │    │  • JWKS Cache   │    │  • Secrets   │                          │
│  │  • Traces    │    │  • App Cache    │    │  • Certs     │                          │
│  │  • Data      │    │                 │    │              │                          │
│  └──────────────┘    └─────────────────┘    └──────────────┘                          │
│                                                                                         │
└─────────────────────────────────────────────────────────────────────────────────────────┘

Legend:
  ~──────~ = HTTP / gRPC
  ════════ = Async Messaging
  ◄────► = Bi-directional
```

### **Shared Infrastructure & Libraries**

- **Blocks.Genesis** — Core framework library providing unified APIs for logging, messaging, caching, authentication, database access, and middleware
- **Blocks.LMT.Client** — Logging & Monitoring Telemetry client for .NET with Azure Service Bus integration and OpenTelemetry support
- **@seliseblocks/lmt-client** — Node.js/TypeScript observability client for services outside the .NET ecosystem
- **TestDriver** — Shared test utilities and gRPC client implementation

---

## Controllers / Endpoints

### **Authentication Legend**

- **Public** — No authentication required
- **[Authorize]** — JWT Bearer token required (verified against issuer JWKS)
- **[ProtectedEndPoint]** — Custom protected endpoint requiring additional authorization context

---

### **S1Controller — /api/s1/**

Base Path: `/api/s1/`

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/process` | [Authorize] | Process incoming request, publish events to demo_topic, call S2 API, retrieve MongoDB data, execute gRPC call. Returns aggregated HTTP and gRPC results. Query params: `projectKey` (tenant context). |
| GET | `/cert` | [ProtectedEndPoint] | Return current security context and certificate information. Used for protected endpoint validation. |

---

### **S2Controller — /api/s2/**

Base Path: `/api/s2/`

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/process` | Public | Process request, publish event to queue `test_from_cloud_queue_1`, return current security context. |
| GET | `/process_1` | Public | Alternative process endpoint, return only security context without event publishing. |

---

### **GreeterService — gRPC (Greeter.Greeter)**

Service: `GrpcServiceTestTemp.Greeter`

| RPC Method | Request | Response | Description |
|---|---|---|---|
| SayHello | HelloRequest (name: string) | HelloReply (message: string) | Returns serialized BlocksContext as JSON. Demonstrates gRPC integration with security context. |

---

## Tech Stack

| Layer | Technology / Version |
|---|---|
| **Runtime** | .NET 9.0 |
| **Language** | C# 12.0, TypeScript 5.3.3 |
| **API Framework** | ASP.NET Core 9.0 |
| **Authentication** | JWT Bearer (Microsoft.AspNetCore.Authentication.JwtBearer 9.0.9) |
| **RPC Framework** | gRPC 2.71.0, Protobuf 3.32.1 |
| **Database** | MongoDB Driver 3.5.0 |
| **Cache** | StackExchange.Redis 2.9.25 |
| **Message Broker** | Azure Service Bus 7.20.1, RabbitMQ.Client 7.1.2 |
| **Logging** | Serilog 4.3.0 with MongoDB sink |
| **Tracing** | OpenTelemetry 1.13.1 with ASP.NET Core & HTTP Instrumentation |
| **Metrics** | OpenTelemetry (no separate metrics service; MongoDB storage) |
| **Secret Management** | Azure Identity 1.17.0, Azure.Security.KeyVault.Secrets 4.8.0 |
| **API Documentation** | Swashbuckle.AspNetCore 9.0.6 |
| **Resilience** | Polly 8.6.4 |
| **Containerization** | Docker (multi-stage builds, .NET 9.0 SDK & Runtime) |
| **Environment Config** | DotNetEnv 3.1.1 |

---

## Prerequisites

| Tool | Minimum Version | Notes |
|---|---|---|
| .NET SDK | 9.0.0 | Required to build and run services; download from [dotnet.microsoft.com](https://dotnet.microsoft.com) |
| Docker | 24.0 | Required for containerized deployments; includes docker-compose |
| Docker Compose | 2.20 | Optional; for local multi-service orchestration |
| MongoDB | 6.0 | Required for logging, metrics, tracing, and data persistence; can run in Docker |
| Redis | 7.0 | Required for caching; can run in Docker |
| Azure CLI | 2.50 | Optional; required for Azure Service Bus or Key Vault integration |
| Node.js | 16.0.0 | Required only for Node.js observability client (@seliseblocks/lmt-client) |
| npm / yarn | 8.0 / 1.22 | Required only for Node.js projects using LMT client |

---

## Installation

### 1. **Clone the Repository**

```bash
git clone https://github.com/SELISEdigitalplatforms/blocks-genesis-net.git
cd blocks-genesis-net
```

### 2. **Restore Dependencies**

```bash
cd src
dotnet restore blocks-genesis-net.sln
```

For Node.js observability client:

```bash
cd node
npm install
npm run build
```

### 3. **Build the Solution**

```bash
cd src
dotnet build -c Release blocks-genesis-net.sln
```

---

## Configuration — Environment Variables

The application supports **two mutually exclusive approaches** for managing secrets and configuration:

### **Approach Selection**

- **Option A: Local Environment Variables** — Use during local development. Set `BlocksSecret__*` prefixed variables.
- **Option B: Azure Key Vault** — Use in production/staging. Set `VaultType.Azure` in code and store plain variable names in Key Vault (no prefix).

**Only ONE approach should be active at a time.** The framework automatically detects and uses the configured approach based on the presence of `BlocksSecret__` prefixed variables or vault configuration.

---

### **Option A: Local Environment Variables**

Set the following environment variables on your local machine or `.env` file (loaded via `DotNetEnv`):

```bash
# Cache (Redis)
BlocksSecret__CacheConnectionString=localhost:6379,password=yourpassword,abortConnect=false,connectTimeout=50000,syncTimeout=50000

# Message Broker (Azure Service Bus or RabbitMQ)
# For Azure Service Bus:
BlocksSecret__MessageConnectionString=Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-key

# For RabbitMQ:
BlocksSecret__MessageConnectionString=amqp://username:password@hostname:5672/

# Observability Backends (MongoDB)
BlocksSecret__LogConnectionString=mongodb://localhost:27017/
BlocksSecret__MetricConnectionString=mongodb://localhost:27017/
BlocksSecret__TraceConnectionString=mongodb://localhost:27017/

# Database Names
BlocksSecret__LogDatabaseName=Logs
BlocksSecret__MetricDatabaseName=Metrics
BlocksSecret__TraceDatabaseName=Traces

# Application Database (MongoDB)
BlocksSecret__DatabaseConnectionString=mongodb://localhost:27017/
BlocksSecret__RootDatabaseName=ApplicationData

# Security Configuration
BlocksSecret__EnableHsts=false
```

---

### **Option B: Azure Key Vault (Production / Staging)**

When deploying to a production or staging environment configured with Azure Key Vault:

1. Ensure your application is running on Azure (App Service, AKS, Container Instances) with a managed identity.
2. Create the following secrets in your Azure Key Vault using **flat names** (no `BlocksSecret__` prefix):

```
CacheConnectionString
MessageConnectionString
LogConnectionString
MetricConnectionString
TraceConnectionString
LogDatabaseName
MetricDatabaseName
TraceDatabaseName
DatabaseConnectionString
RootDatabaseName
EnableHsts
```

3. In your `Program.cs`, ensure `VaultType.Azure` is set:

```csharp
await ApplicationConfigurations.ConfigureLogAndSecretsAsync(_serviceName, VaultType.Azure);
```

The framework automatically resolves secrets from Key Vault. **No code changes are needed to switch approaches — only the presence or absence of `BlocksSecret__` prefixed variables matters.**

---

### **Variable Reference Table**

| Variable | Purpose |
|---|---|
| CacheConnectionString | Redis (or compatible) cache — used for JWT token caching, JWKS caching, and application data caching. Connection string includes password, connection timeout, and sync timeout parameters. |
| MessageConnectionString | Message broker endpoint for publishing and consuming domain events. Supports both Azure Service Bus and RabbitMQ. Format varies by broker type. |
| LogConnectionString | Connection to the centralized log storage backend (MongoDB). Logs from all services are aggregated here with tenant and request correlation. |
| MetricConnectionString | Connection to the metrics collection backend (MongoDB). System metrics, business metrics, and performance counters are stored here. |
| TraceConnectionString | Connection to the distributed tracing backend (MongoDB). Traces with full context propagation across service boundaries are stored here. |
| LogDatabaseName | Database / collection name within the log store. Example: `Logs`. Each log entry includes ServiceName, Timestamp, TenantId, RequestId. |
| MetricDatabaseName | Database / collection name within the metrics store. Example: `Metrics`. Stores OpenTelemetry metric snapshots. |
| TraceDatabaseName | Database / collection name within the trace store. Example: `Traces`. Stores OpenTelemetry spans with parent-child relationships. |
| DatabaseConnectionString | Primary application database connection (MongoDB). Used for storing business entities, users, tokens, and resources. |
| RootDatabaseName | Root / default database name on the primary data store. Example: `ApplicationData`. Used for initialization and schema management. |
| EnableHsts | Enables HTTP Strict Transport Security (HSTS) middleware. Set to `true` in production, `false` for local development. Passed as boolean. |

---

## Running the Project Locally

### **Step 1: Set Environment Variables**

#### **Linux / macOS (Bash)**

Create or update your `.env` file and source it:

```bash
cat > .env << 'EOF'
BlocksSecret__CacheConnectionString=localhost:6379
BlocksSecret__MessageConnectionString=amqp://guest:guest@localhost:5672/
BlocksSecret__LogConnectionString=mongodb://localhost:27017/
BlocksSecret__MetricConnectionString=mongodb://localhost:27017/
BlocksSecret__TraceConnectionString=mongodb://localhost:27017/
BlocksSecret__LogDatabaseName=Logs
BlocksSecret__MetricDatabaseName=Metrics
BlocksSecret__TraceDatabaseName=Traces
BlocksSecret__DatabaseConnectionString=mongodb://localhost:27017/
BlocksSecret__RootDatabaseName=ApplicationData
BlocksSecret__EnableHsts=false
EOF

# Load into current shell
export $(cat .env | xargs)
```

Or run the provided setup script:

```bash
source src/Genesis/setup_env.sh
```

#### **Windows (PowerShell)**

```powershell
$env:BlocksSecret__CacheConnectionString = "localhost:6379"
$env:BlocksSecret__MessageConnectionString = "amqp://guest:guest@localhost:5672/"
$env:BlocksSecret__LogConnectionString = "mongodb://localhost:27017/"
$env:BlocksSecret__MetricConnectionString = "mongodb://localhost:27017/"
$env:BlocksSecret__TraceConnectionString = "mongodb://localhost:27017/"
$env:BlocksSecret__LogDatabaseName = "Logs"
$env:BlocksSecret__MetricDatabaseName = "Metrics"
$env:BlocksSecret__TraceDatabaseName = "Traces"
$env:BlocksSecret__DatabaseConnectionString = "mongodb://localhost:27017/"
$env:BlocksSecret__RootDatabaseName = "ApplicationData"
$env:BlocksSecret__EnableHsts = "false"
```

### **Step 2: Start Infrastructure Services (Docker)**

```bash
docker run -d \
  --name mongo \
  -p 27017:27017 \
  mongo:latest

docker run -d \
  --name redis \
  -p 6379:6379 \
  redis:latest redis-server --appendonly yes

docker run -d \
  --name rabbitmq \
  -p 5672:5672 \
  -p 15672:15672 \
  rabbitmq:3-management

# Wait for services to be ready (~10 seconds)
sleep 10
```

### **Step 3: Build & Run Each Service**

Open separate terminals for each service.

#### **Terminal 1: ApiOne**

```bash
cd src/Api1
dotnet run --project ApiOne.csproj
```

- **Default Port:** HTTP1_PORT=4000, HTTP2_PORT=4001
- **Swagger UI:** http://localhost:4000/swagger

#### **Terminal 2: ApiTwo**

```bash
cd src/Apis
dotnet run --project ApiTwo.csproj
```

- **Default Port:** 5131
- **Swagger UI:** http://localhost:5131/swagger
- **gRPC Port:** 5131 (same)

#### **Terminal 3: WorkerOne**

```bash
cd src/Workers
dotnet run --project WorkerOne.csproj
```

- **No HTTP Port** — runs as background service consuming from Azure Service Bus topics `demo_topic`, `demo_topic_1` and queue `demo_queue`

#### **Terminal 4: WorkerTwo**

```bash
cd src/WorkerTwo
dotnet run --project WorkerTwo.csproj
```

- **No HTTP Port** — runs as background service consuming from RabbitMQ queue `test_from_cloud_queue_1`

### **Step 4 (Alternative): Docker**

Build and run all services in containers:

```bash
# Build API image
docker build -f Apis.Dockerfile -t blocks-genesis-api:latest .

# Build Worker image
docker build -f Workers.Dockerfile -t blocks-genesis-worker:latest .

# Run ApiOne with env vars
docker run -d \
  --name api-one \
  -p 4000:4000 \
  -e BlocksSecret__CacheConnectionString="redis:6379" \
  -e BlocksSecret__MessageConnectionString="am://rabbitmq:5672/" \
  -e BlocksSecret__DatabaseConnectionString="mongodb://mongo:27017/" \
  blocks-genesis-api:latest

# Run ApiTwo
docker run -d \
  --name api-two \
  -p 5131:5131 \
  -e BlocksSecret__CacheConnectionString="redis:6379" \
  -e BlocksSecret__MessageConnectionString="amqp://rabbitmq:5672/" \
  -e BlocksSecret__DatabaseConnectionString="mongodb://mongo:27017/" \
  blocks-genesis-api:latest

# Run WorkerOne
docker run -d \
  --name worker-one \
  -e BlocksSecret__MessageConnectionString="Endpoint=sb://your-namespace.servicebus.windows.net/..." \
  -e BlocksSecret__DatabaseConnectionString="mongodb://mongo:27017/" \
  blocks-genesis-worker:latest

# Run WorkerTwo
docker run -d \
  --name worker-two \
  -e BlocksSecret__MessageConnectionString="amqp://rabbitmq:5672/" \
  -e BlocksSecret__DatabaseConnectionString="mongodb://mongo:27017/" \
  blocks-genesis-worker:latest
```

---

## Usage

### **Base URLs**

| Service | Endpoint | Purpose |
|---|---|---|
| ApiOne | http://localhost:4000 | Main API service with REST controllers |
| ApiOne Swagger | http://localhost:4000/swagger | Interactive API documentation for ApiOne |
| ApiOne Health | http://localhost:4000/health | Health check endpoint |
| ApiTwo | http://localhost:5131 | Secondary API service with gRPC support |
| ApiTwo Swagger | http://localhost:5131/swagger | Interactive API documentation for ApiTwo |
| ApiTwo gRPC | http://localhost:5131 (h2c) | gRPC endpoint for Greeter service |
| Logs | MongoDB: Logs collection | View structured logs in MongoDB |
| Metrics | MongoDB: Metrics collection | View metrics data in MongoDB |
| Traces | MongoDB: Traces collection | View distributed traces in MongoDB |

### **Example API Calls**

**Public endpoint (ApiTwo):**

```bash
curl -X GET http://localhost:5131/api/s2/process
```

**Protected endpoint (ApiOne with JWT):**

```bash
# Requires valid JWT token
curl -X GET http://localhost:4000/api/s1/process \
  -H "Authorization: Bearer <your-jwt-token>" \
  -H "Content-Type: application/json"
```

**gRPC call (ApiTwo SayHello):**

```bash
grpcurl -plaintext \
  -d '{"name":"Blocks"}' \
  localhost:5131 greet.Greeter/SayHello
```

Refer to the Swagger UI (`/swagger`) for full request/response schemas, required fields, and live API testing.

---

## Contributing

Contributions are welcome. Please follow these steps:

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/my-feature`
3. Commit your changes using [Conventional Commits](https://www.conventionalcommits.org/)
4. Push your branch and open a Pull Request against `dev`
5. Ensure all tests pass before submitting: `dotnet test src/blocks-genesis-net.sln`

Please read [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) before submitting a PR.

---

## License

This project is licensed under the terms of the [MIT License](LICENSE).

---

## Maintainers

For questions, issues, or security concerns, please open a [GitHub Issue](https://github.com/SELISEdigitalplatforms/blocks-genesis-net/issues) or review [SECURITY.md](SECURITY.md) for responsible disclosure guidelines.

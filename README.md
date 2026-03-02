# blocks-genesis-net

SELISE `<blocks />` Genesis is a .NET 9 service foundation for building APIs and workers with built-in configuration, middleware, messaging, cache, observability, and multi-tenant utilities.

This repository includes:

- the core framework package (`src/Genesis`)
- sample API and worker services (`src/Api1`, `src/Apis`, `src/Workers`, `src/WorkerTwo`)
- test and driver projects (`src/UnitTest`, `src/TestDriver`)
- a Node.js LMT client package (`node/`)

---

## Solution Projects

- `src/Genesis` → reusable framework (`SeliseBlocks.Genesis`)
- `src/Blocks.LMT.Client` → .NET LMT client used by Genesis
- `src/Api1` → API sample with HTTP endpoints and framework setup
- `src/Apis` → API sample with gRPC endpoint
- `src/Workers` → worker sample using Azure Service Bus configuration
- `src/WorkerTwo` → worker sample using RabbitMQ configuration
- `src/UnitTest` → unit test project
- `src/TestDriver` → helper driver utilities
- `node/` → `@seliseblocks/lmt-client` TypeScript package

---

## Prerequisites

- **.NET SDK 9.0+**
- **Node.js 16+** (for `node/` package)
- **MongoDB / Redis / Message Broker access** for full local integration tests

Optional:

- Docker (if you containerize services)

---

## Local Setup

<details>
	<summary><strong>1. Clone and Restore</strong></summary>

```bash
git clone https://github.com/SELISEdigitalplatforms/blocks-genesis-net.git
cd blocks-genesis-net
dotnet restore src/blocks-genesis-net.sln
```

</details>

---

<details>
	<summary><strong>2. Configure Environment Variables</strong></summary>

Genesis reads `BlocksSecret__*` values from environment variables.

Quick local setup script:

```bash
cd src/Genesis
source setup_env.sh
```

Common variables used by the samples:

- `BlocksSecret__CacheConnectionString`
- `BlocksSecret__DatabaseConnectionString`
- `BlocksSecret__RootDatabaseName`
- `BlocksSecret__LogConnectionString`
- `BlocksSecret__MetricConnectionString`
- `BlocksSecret__TraceConnectionString`
- `BlocksSecret__MessageConnectionString`
- `BlocksSecret__EnableHsts`

For production, configure these values from your secure secret store (for example Azure Key Vault / Vault).

</details>

---

<details>
	<summary><strong>3. Build the Solution</strong></summary>

```bash
dotnet build src/blocks-genesis-net.sln
```

</details>

---

<details>
	<summary><strong>4. Run Sample Services</strong></summary>

Run any service independently from repository root:

```bash
dotnet run --project src/Api1/ApiOne.csproj
dotnet run --project src/Apis/ApiTwo.csproj
dotnet run --project src/Workers/WorkerOne.csproj
dotnet run --project src/WorkerTwo/WorkerTwo.csproj
```

Notes:

- `src/Api1` uses framework API setup and sample controller(s)
- `src/Apis` hosts a gRPC service (`GreeterService`)
- `src/Workers` demonstrates Azure Service Bus consumer registration
- `src/WorkerTwo` demonstrates RabbitMQ consumer subscriptions

</details>

---

<details>
	<summary><strong>5. Run Unit Tests</strong></summary>

```bash
dotnet test src/UnitTest/XUnitTest.csproj
```

</details>

---

## Genesis Usage (Service Bootstrap)

Typical startup flow in APIs/workers:

1. `ApplicationConfigurations.ConfigureLogAndSecretsAsync(...)`
2. `ApplicationConfigurations.ConfigureApiEnv(...)` or worker host setup
3. `ApplicationConfigurations.ConfigureServices(...)`
4. `ApplicationConfigurations.ConfigureApi(...)` or `ConfigureWorker(...)`
5. Run host/app

See runnable examples in:

- `src/Api1/Program.cs`
- `src/Apis/Program.cs`
- `src/Workers/Program.cs`
- `src/WorkerTwo/Program.cs`

---

## Node Package (`node/`)

This repo also contains `@seliseblocks/lmt-client` for TypeScript/Node workloads.

```bash
cd node
npm install
npm run build
```

For usage, see `node/README.md` and `node/example/index.ts`.

---

## Additional Documentation

- Framework package usage: `src/Genesis/README.md`
- Priority unit test notes: `src/XUnitTest/README-Priority-Unit-Test-Targets.md`
- Contribution and security policies: `CONTRIBUTING.md`, `SECURITY.md`
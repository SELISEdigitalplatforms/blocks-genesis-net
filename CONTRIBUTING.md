# Contributing to blocks-genesis-net

Thank you for your interest in contributing to **blocks-genesis-net**! Your contributions help improve this multi-tenant microservices framework for everyone. Whether you're reporting a bug, suggesting an enhancement, or submitting code changes, we welcome your input.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [How to Contribute](#how-to-contribute)
  - [Reporting Issues](#reporting-issues)
  - [Submitting Pull Requests](#submitting-pull-requests)
- [Development Setup](#development-setup)
- [Branching Strategy](#branching-strategy)
- [Git Guidelines](#git-guidelines)
- [Coding Guidelines](#coding-guidelines)
- [Testing](#testing)
- [Code Review Process](#code-review-process)
- [License](#license)

## Code of Conduct

Please read and follow our [Code of Conduct](./CODE_OF_CONDUCT.md). By participating in this project, you agree to abide by its terms.

## How to Contribute

### Reporting Issues

If you encounter a bug or have a feature request, please [open an issue](https://github.com/SELISEdigitalplatforms/blocks-genesis-net/issues/new) and include:

**For Bugs:**
- **Description**: Clear, concise description of the issue
- **Steps to Reproduce**: Detailed steps to replicate the problem
- **Expected Behavior**: What should happen
- **Actual Behavior**: What actually happens
- **Environment**: .NET SDK version, OS, Docker version (if applicable), local dependency versions
- **Logs/Error Output**: Relevant error messages or stack traces
- **Type**: Label as `bug`

**For Feature Requests:**
- **Use Case**: Clear explanation of the feature and its use case
- **Proposed Solution**: Your suggested implementation (if any)
- **Alternative Approaches**: Any alternative approaches considered
- **Type**: Label as `enhancement`

### Submitting Pull Requests

1. **Fork the Repository**: Click the "Fork" button at the top right of the repository page.
2. **Clone Your Fork**: Clone your forked repository to your local machine.
   ```bash
   git clone https://github.com/SELISEdigitalplatforms/blocks-genesis-net.git
   cd blocks-genesis-net
   ```
3. **Create a Branch**: Create a new branch for your feature or bugfix (see [Branching Strategy](#branching-strategy)).
   ```bash
   git checkout -b feature/your-feature-name
   ```
4. **Set up Development Environment**: Follow [Development Setup](#development-setup).
5. **Make Changes**: Implement your changes following [Coding Guidelines](#coding-guidelines).
6. **Write/Update Tests**: Ensure new code has tests (see [Testing](#testing)).
7. **Run Tests**: Verify all tests pass locally.
   ```bash
   dotnet test src/blocks-genesis-net.sln
   ```
8. **Commit Changes**: Follow [Git Guidelines](#git-guidelines) for commit messages.
9. **Push to GitHub**: Push your changes to your forked repository.
   ```bash
   git push origin feature/your-feature-name
   ```
10. **Open a Pull Request**: Navigate to the original repository and click "New Pull Request". Link any related issues.

## Development Setup

### 1. Install Prerequisites

```bash
# Verify required tools
dotnet --version
git --version
docker --version
```

Install and run the local dependencies required by the project, such as MongoDB, Redis-compatible cache, and RabbitMQ or Azure Service Bus-compatible local infrastructure, before starting the services.

### 2. Restore Dependencies

```bash
dotnet restore src/blocks-genesis-net.sln
```

### 3. Configure Environment

Set the required environment variables for local development. At minimum, configure database, cache, message broker, and environment selection.

Using Bash:
```bash
export BlocksSecret__DatabaseConnectionString="mongodb://localhost:27017"
export BlocksSecret__RootDatabaseName="BlocksRootDb"
export BlocksSecret__CacheConnectionString="redis://localhost:6379"
export BlocksSecret__MessageConnectionString="amqp://guest:guest@localhost:5672"
export ASPNETCORE_ENVIRONMENT="Development"
```

Using PowerShell:
```powershell
$env:BlocksSecret__DatabaseConnectionString = "mongodb://localhost:27017"
$env:BlocksSecret__RootDatabaseName = "BlocksRootDb"
$env:BlocksSecret__CacheConnectionString = "redis://localhost:6379"
$env:BlocksSecret__MessageConnectionString = "amqp://guest:guest@localhost:5672"
$env:ASPNETCORE_ENVIRONMENT = "Development"
```

For the full list of supported variables, see [README.md](./README.md).

### 4. Build the Solution

```bash
dotnet build src/blocks-genesis-net.sln --configuration Release
```

### 5. Verify Installation

```bash
dotnet test src/UnitTest/XUnitTest.csproj -v normal
```

To run the application locally:

```bash
# API hosts
dotnet run --project src/Api1/ApiOne.csproj
dotnet run --project src/Apis/ApiTwo.csproj

# Worker hosts
dotnet run --project src/Workers/WorkerOne.csproj
dotnet run --project src/WorkerTwo/WorkerTwo.csproj
```

## Branching Strategy

We follow **Git Flow** for branching:

- `main`: Production-ready, stable releases.
- `dev`: Active development branch (default for PRs).
- `feature/*`: New features branching from `dev` (e.g., `feature/schema-caching`).
- `bugfix/*`: Bug fixes branching from `dev` (e.g., `bugfix/null-tenant-context`).
- `hotfix/*`: Emergency fixes branching from `main` for critical production issues.
- `docs/*`: Documentation updates (e.g., `docs/api-reference`).

All PRs should target the `dev` branch unless otherwise agreed.

## Git Guidelines

We follow **Conventional Commits** specification for standardized commit messages.

### Commit Message Format

```
<type>(<scope>): <subject>

<body>

<footer>
```

### Types

- `feat`: A new feature
- `fix`: A bug fix
- `docs`: Documentation only changes
- `style`: Changes that don't affect code logic (formatting, whitespace, semicolons)
- `refactor`: Code change that refactors without feature/fix (no functional changes)
- `perf`: Performance improvements
- `test`: Adding/updating tests
- `chore`: Build process, dependency updates, tooling changes

### Scope (optional)

Indicate the affected component or module:
- `api`: API controllers and middleware
- `worker`: Background job processing and consumers
- `datagateway`: Schema, validation, and policy services
- `storage`: File storage and DMS operations
- `driver`: Driver service integrations
- `config`: Configuration/settings
- `test`: Test infrastructure

### Subject Line

- Use imperative mood ("add feature", not "added feature")
- Do not capitalize first letter
- Do not end with a period
- Maximum 50 characters
- Be specific and descriptive

### Body

- Use imperative mood
- Explain **what** and **why**, not **how**
- Wrap at 72 characters
- Separate each logical change with a blank line

### Footer

Reference related issues or breaking changes:
```
Fixes #123
Closes #456
BREAKING CHANGE: description of breaking change
```

### Examples

```
feat(api): add schema aggregation endpoint

- Add endpoint that returns schemas with access summaries
- Include pagination and filter support
- Update API documentation examples

Closes #42
```

```
fix(worker): handle null tenant id during import

The schema import workflow could throw when tenant context was missing.
Add validation and return a safer error path before processing starts.

Fixes #189
```

```
docs: update local setup instructions

Update environment variable examples and worker startup steps.
```

## Coding Guidelines

### C# Style and Format

- **C# Conventions**: Follow Microsoft's C# coding conventions.
- **Line Length**: Maximum 120 characters (project convention).
- **Imports**:
  - Organize imports in three groups: framework, third-party, local (separated by blank lines).
  - Use explicit, clear namespaces.
  - Avoid circular dependencies between projects.
- **Async/Await**: Use `async`/`await` consistently for asynchronous methods. Ensure proper exception handling in async flows.
- **Nullable Reference Types**: Respect nullable reference types and model nullability explicitly.
  ```csharp
  public async Task<SchemaDefinition?> GetSchemaAsync(string id)
  {
      return await repository.GetByIdAsync(id);
  }
  ```

### Project Structure

When adding new features, follow the existing structure:

```
src/
├── Api1/
│   ├── Program.cs
│   └── S1Controller.cs
├── Apis/
│   ├── Program.cs
│   ├── S2Controller.cs
│   └── GreeterService.cs
├── Genesis/
│   ├── Auth/
│   ├── Cache/
│   ├── Database/
│   └── Message/
├── Workers/
│   ├── Program.cs
│   ├── W1Consumer.cs
│   └── W2Consumer.cs
├── WorkerTwo/
│   ├── Program.cs
│   ├── W1Consumer.cs
│   └── W2Consumer.cs
└── UnitTest/
   ├── Cache/
   ├── Database/
   ├── Message/
   └── Tenant/
```

**For new features:**
1. Add contracts, models, and services in the appropriate `src/Genesis/` module.
2. Add or extend controllers in `src/Api1/` or `src/Apis/` when exposing HTTP or gRPC endpoints.
3. Add worker consumers in `src/Workers/` or `src/WorkerTwo/` if asynchronous processing is required.
4. Add matching tests in `src/UnitTest/` mirroring the source structure.

### API Conventions

- **Endpoint Naming**: Use resource-oriented HTTP endpoints and follow the existing REST + GraphQL patterns.
- **Response Models**: Use clear request/response contracts and keep validation close to the domain layer.
- **Status Codes**:
  - `200 OK` for successful GET/PUT operations
  - `201 Created` for successful POST operations when creating resources
  - `204 No Content` for successful DELETE operations where no payload is returned
  - `400 Bad Request` for validation errors
  - `404 Not Found` for missing resources
  - `500 Internal Server Error` for server errors
- **Error Responses**: Return structured error responses consistent with current controller behavior.
- **Documentation**: Add XML comments and keep Swagger/OpenAPI-visible endpoints understandable.
  ```csharp
  /// <summary>
  /// Creates a new schema definition.
  /// </summary>
  [HttpPost("/schemas/define")]
  public async Task<IActionResult> DefineSchema(CreateSchemaRequest request)
  {
      // Implementation
  }
  ```

### Logging

- Use the .NET logging abstractions (`ILogger<T>`) instead of console prints.
- Use appropriate log levels: `LogDebug`, `LogInformation`, `LogWarning`, `LogError`, `LogCritical`.
  ```csharp
  logger.LogInformation("Schema created: {SchemaId}", schemaId);
  logger.LogError(exception, "Failed to import schema for project {ProjectKey}", projectKey);
  ```

### Context and Multi-Tenancy

- Always respect project and tenant context when processing requests.
- Avoid hardcoding tenant IDs, project keys, URLs, or secret values.
- Keep schema and storage operations aligned with the current request context and configured providers.

### Error Handling

- Use specific exception types where possible.
- Provide meaningful error messages.
- Log exceptions with relevant context.
  ```csharp
  if (string.IsNullOrWhiteSpace(request.ProjectKey))
  {
      return BadRequest("Project key is required.");
  }
  ```

## Testing

### Test Organization

Tests are organized in `src/UnitTest/` to mirror source structure:

```
src/UnitTest/
├── Cache/
├── Configuration/
├── Database/
├── Lmt/
├── Message/
└── Tenant/
```

### Writing Tests

- **Framework**: Use `xUnit` with `Moq` for mocking and `FluentAssertions` for expressive assertions.
- **File Naming**: Test files should generally be named after the target type with a `Tests` suffix.
- **Method Naming**: Test methods should clearly describe the expected behavior.
- **Shared Helpers**: Use `TestHelpers` for reusable fixtures and utilities.
- **Mocking**: Mock external dependencies and isolate domain behavior.

Example:

```csharp
[Fact]
public async Task DefineSchema_WithValidRequest_ReturnsSuccess()
{
    var request = new CreateSchemaRequest { Name = "Product" };

    var result = await service.DefineSchemaAsync(request);

    result.Should().NotBeNull();
}
```

### Running Tests

Run all tests:
```bash
dotnet test src/blocks-genesis-net.sln
```

Run specific test project:
```bash
dotnet test src/UnitTest/XUnitTest.csproj
```

Run with coverage:
```bash
dotnet test src/UnitTest/XUnitTest.csproj --collect:"XPlat Code Coverage"
```

### Test Requirements

- New features must include tests.
- Bug fixes should include regression tests.
- Aim for >80% code coverage on core service layers where practical.
- All tests must pass before PR submission.

## Code Review Process

All PRs undergo review to maintain quality:

1. **PR Submission**:
   - Ensure PR is focused on a single feature/fix.
   - Link related issues.
   - Provide clear description of changes.
   - Verify all tests pass locally.

2. **Automated Checks**:
   - CI/CD will run tests and linting.
   - Code must pass all checks.

3. **Peer Review**:
   - At least one maintainer must review and approve.
   - Address review comments promptly.
   - Request re-review after making changes.

4. **Merge Process**:
   - Once approved and all checks pass, the PR is merged into `dev`.
   - Use "Squash and merge" for feature PRs to keep history clean.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](./LICENSE).

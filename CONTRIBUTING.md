# Contributing to blocks-genesis-net

Thank you for your interest in contributing to **blocks-genesis-net**! Your contributions help improve this project for everyone. Whether you're reporting a bug, suggesting an enhancement, or submitting code changes, we welcome your input.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Public API Stability](#public-api-stability)
- [How to Contribute](#how-to-contribute)
  - [Reporting Issues](#reporting-issues)
  - [Submitting Pull Requests](#submitting-pull-requests)
- [Branching Strategy](#branching-strategy)
- [Building and Testing](#building-and-testing)
- [Git Guidelines](#git-guidelines)
- [Code Review Process](#code-review-process)
- [License](#license)

## Public API Stability

This repository ships the `SeliseBlocks.Genesis` and `SeliseBlocks.LMT.Client` NuGet packages, which are consumed by ten downstream Blocks service repositories. **Any change to a public API is a breaking change for all of them.** That includes renaming or removing a public type or member, changing a signature, changing a default parameter value, changing a public type's namespace, and changing observable behavior of a public member. Do not make such a change casually: propose it in an issue first, and expect it to require a coordinated release. Superseded members are kept with `[Obsolete]` markers for at least one release before removal.

## Code of Conduct

Please read and follow our [Code of Conduct](./CODE_OF_CONDUCT.md). By participating in this project, you agree to abide by its terms.

## How to Contribute

### Reporting Issues

If you encounter a bug or any issue, please report it by [opening an issue](https://github.com/SELISEdigitalplatforms/blocks-genesis-net/issues/new) and include the following details:

- **Description**: A clear and concise description of the bug.
- **Steps to Reproduce**: Steps to replicate the issue.
- **Expected Behavior**: What should happen.
- **Actual Behavior**: What actually happens.
- **Screenshots**: If applicable, attach screenshots.
- **Environment**: Specify OS, browser, and versions.
- **Type**: Select type `Bug`
- **Project**: Select Project `Blocks Construct`


### Submitting Pull Requests

1. **Fork the Repository**: Click the "Fork" button at the top right of the repository page.
2. **Clone Your Fork**: Clone your forked repository to your local machine.
   ```bash
   git clone https://github.com/your-username/blocks-genesis-net.git
   cd blocks-genesis-net
   ```
3. **Create a Branch**: Create a new branch from `main` for your feature or bugfix.
   ```bash
   git checkout -b feature/your-feature-name
   ```
4. **Make Changes**: Implement your changes in the codebase.
5. **Commit Changes**: Follow the [Git Guidelines](#git-guidelines) for commit messages.
6. **Push to GitHub**: Push your changes to your forked repository.
   ```bash
   git push origin feature/your-feature-name
   ```
7. **Open a Pull Request**: Navigate to the original repository and click "New Pull Request".

## Branching Strategy

- `main`: The default branch and the released state of the packages. It is protected; changes land only through pull requests.
- `inception`: The active working branch. Day-to-day work is committed here, and pull requests are opened from `inception` into `main`.
- Contributors without write access: fork the repository, branch from `main`, and open a pull request against `main`.

Never force-push or rewrite history on shared branches.

## Building and Testing

From the repository root:

```bash
dotnet restore src/blocks-genesis-net.sln
dotnet build src/blocks-genesis-net.sln
dotnet test src/XUnitTest/XUnitTest.csproj
```

The full test suite must pass before a pull request is opened. To exercise integration scenarios locally, start MongoDB, Redis, and RabbitMQ with `docker-compose up -d` and use `.env.example` as the baseline configuration.

## Git Guidelines

- **Use the Imperative Mood**: Start commit messages with a verb in the imperative mood (e.g., "add", "fix", "update", "remove").
- **Keep Messages Short and Descriptive**: The subject line should be concise (50 characters or less) and clearly describe the change.
- **Separate Subject from Body**: If more detail is needed, separate the subject from the body with a blank line. The body should explain the "what" and "why" of the changes.
- **Lowercase Commit Message**: Keep the commit message in lowercase.
- **Avoid Ending with a Period**: Do not end the subject line with a period.
- **Reference Issues and Pull Requests**: Reference related issues or pull requests in the body of the commit message (e.g., "fixes #123" or "see pr #456").
- **Use Conventional Commits**: Follow the Conventional Commits specification for a standardized commit message format. Types include `feat`, `fix`, `docs`, `style`, `refactor` and `test`.

Example of a well-structured commit message:
```
feat(auth): add user authentication - issue(#423)

- implement JWT-based authentication
- add login and registration endpoints
- update user model to include password hashing
```

## Code Review Process

All PRs undergo review to maintain quality. Review steps:

1. **PR Submission**: Ensure PRs are small and well-documented.
2. **Automated Checks**: CI/CD will run tests and linting.
3. **Peer Review**: At least one maintainer must approve the PR.
4. **Merge Process**: Once approved, the PR is merged into `main`.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](./LICENSE).


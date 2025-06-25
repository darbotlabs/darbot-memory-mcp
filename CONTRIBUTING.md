# Contributing to Darbot Memory MCP

We welcome contributions to the Darbot Memory MCP project! This document provides guidelines for contributing.

## Getting Started

1. **Fork the repository** on GitHub
2. **Clone your fork** locally
3. **Create a feature branch** from `main`
4. **Make your changes** following our coding standards
5. **Test your changes** thoroughly
6. **Submit a pull request**

## Development Setup

### Prerequisites

- .NET SDK 8.0 or later
- PowerShell 7.x
- Docker 24.x (for container testing)
- Git

### Initial Setup

```bash
git clone https://github.com/your-fork/darbot-memory-mcp.git
cd darbot-memory-mcp
pwsh ./scripts/bootstrap.ps1   # installs git hooks, tools/nbgv
dotnet restore
dotnet build --configuration Release
```

## Development Workflow

### 1. Pre-commit Checks

Run before committing any changes:

```bash
pwsh ./scripts/pre-commit.ps1
```

This will:
- Format code using `dotnet format`
- Run unit tests
- Check for common issues

### 2. Testing Requirements

All new features require:
- **Unit tests** for business logic
- **Integration tests** for end-to-end scenarios
- Tests should have good code coverage
- Tests must pass on all supported platforms

### 3. Code Style

- Follow Microsoft C# coding conventions
- Use meaningful variable and method names
- Add XML documentation for public APIs
- Keep methods small and focused
- Use dependency injection appropriately

### 4. Commit Messages

Use conventional commit format:
```
type(scope): description

- feat: new feature
- fix: bug fix
- docs: documentation changes
- style: formatting changes
- refactor: code restructuring
- test: adding tests
- chore: maintenance tasks
```

## Pull Request Process

1. **Ensure tests pass**: All CI checks must be green
2. **Update documentation**: If you change APIs or behavior
3. **Add/update tests**: For any new functionality
4. **Describe your changes**: Clear PR description with context
5. **Request review**: From maintainers
6. **Address feedback**: Make requested changes promptly

## Code Review Guidelines

- Be respectful and constructive
- Focus on code quality and maintainability
- Suggest improvements, don't just point out problems
- Consider security and performance implications

## Reporting Issues

When reporting bugs:
- Use the issue template
- Include reproduction steps
- Provide environment details
- Include relevant logs/stack traces

## Feature Requests

For new features:
- Describe the use case
- Explain why it's valuable
- Consider implementation complexity
- Discuss potential alternatives

## Security Issues

Report security vulnerabilities privately to: security@darbotlabs.com

Do not create public issues for security problems.

## License

By contributing, you agree that your contributions will be licensed under the MIT License.

## Questions?

- Open a discussion on GitHub
- Join our community chat
- Email: support@darbotlabs.com

Thank you for contributing to Darbot Memory MCP! 🎉
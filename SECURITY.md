# Security Best Practices for Darbot Memory MCP

This document outlines security best practices for developers contributing to the Darbot Memory MCP project.

## Environment Variables and Secrets

### ✅ DO
- Use environment variables for all sensitive configuration values
- Reference environment variables with fallback defaults in configuration files
- Create `.env.example` files to document required environment variables
- Use secrets management systems in production environments

### ❌ DON'T
- Never commit passwords, API keys, or other secrets to the repository
- Don't hardcode sensitive values in source code or configuration files
- Avoid including real credentials in example configurations

## Development Container Setup

The devcontainer uses environment variables for database credentials:

1. Copy `.devcontainer/.env.example` to `.devcontainer/.env`
2. Set appropriate values for your development environment:
   ```bash
   POSTGRES_DEV_PASSWORD=your_secure_password_here
   ```
3. The `.env` file is gitignored and will not be committed

## Configuration Security

### Safe Configuration Patterns
```json
{
  "ConnectionString": "${CONNECTION_STRING:-Server=localhost;Database=dev;}"
}
```

### Environment Variable Usage
```bash
# Production
export DARBOT__STORAGE__AZUREBLOB__CONNECTIONSTRING="DefaultEndpointsProtocol=https;..."
export DARBOT__AUTH__APIKEY="your-secure-api-key"

# Development (in .env file)
POSTGRES_DEV_PASSWORD=secure_dev_password
```

## Security Review Checklist

Before committing code, ensure:
- [ ] No hardcoded passwords or secrets
- [ ] All sensitive values use environment variables
- [ ] `.env` files are not tracked in git
- [ ] Example configurations use placeholder values
- [ ] Private notes or TODO items don't contain sensitive information

## Reporting Security Issues

If you discover a security vulnerability, please follow responsible disclosure:
1. Do not create a public issue
2. Contact the maintainers privately
3. Allow time for the issue to be addressed before public disclosure

## Automated Security Scanning

This repository includes:
- Trivy vulnerability scanning in CI/CD
- SARIF reporting to GitHub Security tab
- Dependency vulnerability alerts

Regular security reviews ensure the repository remains free of sensitive data.
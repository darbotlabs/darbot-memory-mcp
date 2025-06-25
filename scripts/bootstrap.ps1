#!/usr/bin/env pwsh
#
# Bootstrap script for Darbot Memory MCP development environment
# Installs git hooks, tools, and sets up the development environment
#

param(
    [switch]$Force,
    [switch]$Verbose
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Verbose) {
    $VerbosePreference = "Continue"
}

Write-Host "🚀 Bootstrapping Darbot Memory MCP development environment..." -ForegroundColor Green

# Check prerequisites
Write-Verbose "Checking prerequisites..."

$dotnetVersion = dotnet --version 2>$null
if (-not $dotnetVersion) {
    Write-Error ".NET SDK not found. Please install .NET 8.0 SDK."
    exit 1
}

if (-not $dotnetVersion.StartsWith("8.")) {
    Write-Warning ".NET version $dotnetVersion found. .NET 8.0 is recommended."
}

Write-Host "✓ .NET SDK $dotnetVersion found" -ForegroundColor Green

# Check PowerShell version
$psVersion = $PSVersionTable.PSVersion
if ($psVersion.Major -lt 7) {
    Write-Warning "PowerShell $psVersion found. PowerShell 7.x is recommended."
} else {
    Write-Host "✓ PowerShell $psVersion found" -ForegroundColor Green
}

# Install/update dotnet tools
Write-Host "📦 Installing/updating .NET tools..." -ForegroundColor Cyan

$tools = @(
    "nbgv",
    "dotnet-format",
    "dotnet-outdated-tool"
)

foreach ($tool in $tools) {
    Write-Verbose "Installing/updating $tool..."
    try {
        dotnet tool install --global $tool 2>$null
        if ($LASTEXITCODE -ne 0) {
            dotnet tool update --global $tool
        }
        Write-Host "✓ $tool installed/updated" -ForegroundColor Green
    }
    catch {
        Write-Warning "Failed to install/update ${tool}: $($_.Exception.Message)"
    }
}

# Set up git hooks directory if it doesn't exist
$gitHooksDir = ".git/hooks"
if (-not (Test-Path $gitHooksDir)) {
    Write-Verbose "Creating git hooks directory..."
    New-Item -Path $gitHooksDir -ItemType Directory -Force | Out-Null
}

# Install pre-commit hook
$preCommitHook = @"
#!/bin/sh
# Darbot Memory MCP pre-commit hook
# Runs formatting and basic checks before commit

echo "🔍 Running pre-commit checks..."

# Run the pre-commit script
pwsh ./scripts/pre-commit.ps1
if [ `$? -ne 0 ]; then
    echo "❌ Pre-commit checks failed. Commit aborted."
    exit 1
fi

echo "✅ Pre-commit checks passed."
exit 0
"@

$preCommitPath = Join-Path $gitHooksDir "pre-commit"
Write-Verbose "Installing pre-commit hook at $preCommitPath..."
$preCommitHook | Out-File -FilePath $preCommitPath -Encoding utf8 -NoNewline

# Make the hook executable on Unix-like systems
if ($IsLinux -or $IsMacOS) {
    chmod +x $preCommitPath
}

Write-Host "✓ Pre-commit hook installed" -ForegroundColor Green

# Create nbgv version configuration if it doesn't exist
$versionJsonPath = "version.json"
if (-not (Test-Path $versionJsonPath)) {
    Write-Verbose "Creating version.json..."
    $versionConfig = @{
        '$schema' = "https://raw.githubusercontent.com/dotnet/Nerdbank.GitVersioning/master/src/NerdBank.GitVersioning/version.schema.json"
        version = "1.0-preview"
        publicReleaseRefSpec = @(
            "^refs/heads/main$"
            "^refs/heads/release/.*"
        )
        cloudBuild = @{
            setVersionVariables = $true
            buildNumber = @{
                enabled = $true
            }
        }
    }
    
    $versionConfig | ConvertTo-Json -Depth 10 | Out-File -FilePath $versionJsonPath -Encoding utf8
    Write-Host "✓ version.json created" -ForegroundColor Green
}

# Restore packages
Write-Host "📦 Restoring NuGet packages..." -ForegroundColor Cyan
dotnet restore
if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ Packages restored" -ForegroundColor Green
} else {
    Write-Error "Failed to restore packages"
    exit 1
}

# Build the solution
Write-Host "🔨 Building solution..." -ForegroundColor Cyan
dotnet build --configuration Debug --no-restore
if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ Build successful" -ForegroundColor Green
} else {
    Write-Error "Build failed"
    exit 1
}

# Run tests
Write-Host "🧪 Running tests..." -ForegroundColor Cyan
dotnet test --no-build --verbosity quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ All tests passed" -ForegroundColor Green
} else {
    Write-Warning "Some tests failed. Review and fix before committing."
}

Write-Host ""
Write-Host "🎉 Bootstrap completed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  • Run 'dotnet run --project src/Darbot.Memory.Mcp.Api' to start the API"
Write-Host "  • Run 'dotnet test' to run all tests"
Write-Host "  • Run 'pwsh ./scripts/pre-commit.ps1' before committing"
Write-Host "  • See README.md for more information"
Write-Host ""
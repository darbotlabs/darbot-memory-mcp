#!/usr/bin/env pwsh
#
# Pre-commit script for Darbot Memory MCP
# Runs code formatting, linting, and basic checks before commit
#

param(
    [switch]$Fix,
    [switch]$Verbose
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Verbose) {
    $VerbosePreference = "Continue"
}

Write-Host "🔍 Running pre-commit checks for Darbot Memory MCP..." -ForegroundColor Cyan

$success = $true

# Function to run a command and track success
function Invoke-Check {
    param(
        [string]$Name,
        [scriptblock]$Command,
        [switch]$ContinueOnError
    )
    
    Write-Host "  • $Name..." -NoNewline
    
    try {
        $result = & $Command
        if ($LASTEXITCODE -eq 0) {
            Write-Host " ✓" -ForegroundColor Green
            return $true
        } else {
            Write-Host " ❌" -ForegroundColor Red
            if ($result) {
                Write-Host $result -ForegroundColor Red
            }
            if (-not $ContinueOnError) {
                $script:success = $false
            }
            return $false
        }
    }
    catch {
        Write-Host " ❌" -ForegroundColor Red
        Write-Host "    Error: $_" -ForegroundColor Red
        if (-not $ContinueOnError) {
            $script:success = $false
        }
        return $false
    }
}

# 1. Check if solution builds
Write-Host "🔨 Build checks:" -ForegroundColor Yellow
Invoke-Check "Building solution" {
    dotnet build --configuration Debug --verbosity quiet
}

# 2. Format code
Write-Host "`n🎨 Code formatting:" -ForegroundColor Yellow

if ($Fix) {
    Invoke-Check "Formatting code (auto-fix)" {
        dotnet format --verbosity quiet
    } -ContinueOnError
} else {
    Invoke-Check "Checking code format" {
        dotnet format --verify-no-changes --verbosity quiet
    }
}

# 3. Run static analysis
Write-Host "`n🔍 Static analysis:" -ForegroundColor Yellow
Invoke-Check "Running code analysis" {
    dotnet build --configuration Debug --verbosity quiet /p:RunCodeAnalysis=true
} -ContinueOnError

# 4. Run tests
Write-Host "`n🧪 Testing:" -ForegroundColor Yellow
Invoke-Check "Running unit tests" {
    dotnet test --configuration Debug --no-build --verbosity quiet --logger "console;verbosity=minimal"
}

# 5. Check for common issues
Write-Host "`n🔍 Additional checks:" -ForegroundColor Yellow

# Check for TODO/FIXME comments in committed code
Invoke-Check "Checking for TODO/FIXME comments" {
    $todoFiles = git diff --cached --name-only --diff-filter=AM | Where-Object { $_ -match '\.(cs|csproj|json|md)$' } | ForEach-Object {
        $content = git show ":$_" 2>$null
        if ($content -and ($content -match '(TODO|FIXME|HACK)')) {
            $_
        }
    }
    
    if ($todoFiles) {
        Write-Host "    Files with TODO/FIXME comments:" -ForegroundColor Yellow
        $todoFiles | ForEach-Object { Write-Host "      $_" -ForegroundColor Yellow }
        Write-Host "    Consider resolving these before committing." -ForegroundColor Yellow
    }
    
    # Don't fail for TODO comments, just warn
    return $true
} -ContinueOnError

# Check for large files
Invoke-Check "Checking for large files" {
    $largeFiles = git diff --cached --name-only --diff-filter=AM | ForEach-Object {
        $size = (git cat-file -s ":$_" 2>$null)
        if ($size -and $size -gt 1048576) { # 1MB
            [PSCustomObject]@{ File = $_; Size = $size }
        }
    }
    
    if ($largeFiles) {
        Write-Host "    Large files detected:" -ForegroundColor Yellow
        $largeFiles | ForEach-Object { 
            $sizeMB = [math]::Round($_.Size / 1MB, 2)
            Write-Host "      $($_.File) ($sizeMB MB)" -ForegroundColor Yellow 
        }
        return $false
    }
    return $true
} -ContinueOnError

# 6. Check version consistency (if nbgv is available)
if (Get-Command nbgv -ErrorAction SilentlyContinue) {
    Invoke-Check "Checking version consistency" {
        nbgv get-version --format json | Out-Null
    } -ContinueOnError
}

# Summary
Write-Host ""
if ($success) {
    Write-Host "✅ All pre-commit checks passed!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "❌ Some pre-commit checks failed." -ForegroundColor Red
    Write-Host ""
    Write-Host "To fix formatting issues automatically, run:" -ForegroundColor Yellow
    Write-Host "  pwsh ./scripts/pre-commit.ps1 -Fix" -ForegroundColor Yellow
    Write-Host ""
    exit 1
}
#!/usr/bin/env pwsh
#
# Build script for creating MSIX package
# Creates a self-contained Windows executable and packages it for distribution
#

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained = $true,
    [switch]$SkipTests = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "🚀 Building Darbot Memory MCP for MSIX packaging..." -ForegroundColor Green

# Ensure we're in the right directory
if (!(Test-Path "Darbot.Memory.Mcp.sln")) {
    Write-Error "Must run from solution root directory"
    exit 1
}

# Run tests first (unless skipped)
if (-not $SkipTests) {
    Write-Host "🧪 Running tests..." -ForegroundColor Cyan
    dotnet test --configuration $Configuration --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Tests failed. Aborting build."
        exit 1
    }
    Write-Host "✅ Tests passed" -ForegroundColor Green
}

# Create output directory
$outputDir = "publish/msix"
if (Test-Path $outputDir) {
    Remove-Item $outputDir -Recurse -Force
}
New-Item -Path $outputDir -ItemType Directory -Force | Out-Null

# Publish the API project as self-contained
Write-Host "📦 Publishing application..." -ForegroundColor Cyan
dotnet publish src/Darbot.Memory.Mcp.Api/Darbot.Memory.Mcp.Api.csproj `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained:$SelfContained `
    --output "$outputDir/app" `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed"
    exit 1
}

Write-Host "✅ Application published successfully" -ForegroundColor Green

# Create MSIX manifest
Write-Host "📄 Creating MSIX manifest..." -ForegroundColor Cyan

$manifestContent = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:mp="http://schemas.microsoft.com/appx/2014/phone/manifest"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         IgnorableNamespaces="uap mp">
  <Identity Name="DarbotLabs.DarbotMemoryMcp"
            Publisher="CN=Darbot Labs"
            Version="1.0.0.0" />
  <mp:PhoneIdentity PhoneProductId="12345678-1234-1234-1234-123456789012" PhonePublisherId="00000000-0000-0000-0000-000000000000"/>
  <Properties>
    <DisplayName>Darbot Memory MCP</DisplayName>
    <PublisherDisplayName>Darbot Labs</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
    <Description>MCP server for persisting conversational audit trails</Description>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />
  </Dependencies>
  <Resources>
    <Resource Language="x-generate"/>
  </Resources>
  <Applications>
    <Application Id="App"
                 Executable="DarbotMemoryMcp.exe"
                 EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Darbot Memory MCP"
                          Square150x150Logo="Assets\Square150x150Logo.png"
                          Square44x44Logo="Assets\Square44x44Logo.png"
                          Description="MCP server for conversation audit trails"
                          BackgroundColor="transparent">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png"/>
        <uap:SplashScreen Image="Assets\SplashScreen.png" />
      </uap:VisualElements>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient" />
    <Capability Name="privateNetworkClientServer" />
  </Capabilities>
</Package>
"@

$manifestPath = "$outputDir/Package.appxmanifest"
$manifestContent | Out-File -FilePath $manifestPath -Encoding utf8

# Rename executable to match manifest
$originalExe = "$outputDir/app/Darbot.Memory.Mcp.Api.exe"
$newExe = "$outputDir/app/DarbotMemoryMcp.exe"
if (Test-Path $originalExe) {
    Move-Item $originalExe $newExe
}

# Create basic assets (placeholder images)
$assetsDir = "$outputDir/Assets"
New-Item -Path $assetsDir -ItemType Directory -Force | Out-Null

# Create simple placeholder logo files (1x1 transparent PNG)
$pngHeader = [byte[]]@(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0xDA, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82)

$logoFiles = @("StoreLogo.png", "Square150x150Logo.png", "Square44x44Logo.png", "Wide310x150Logo.png", "SplashScreen.png")
foreach ($logoFile in $logoFiles) {
    [System.IO.File]::WriteAllBytes("$assetsDir/$logoFile", $pngHeader)
}

Write-Host "✅ MSIX manifest and assets created" -ForegroundColor Green

# Create installation instructions
$instructionsContent = @"
# Darbot Memory MCP - MSIX Installation

This package contains the Darbot Memory MCP server as a self-contained Windows application.

## Installation Options

### Option 1: PowerShell Installation (Developer Mode Required)
1. Enable Developer Mode in Windows Settings
2. Open PowerShell as Administrator
3. Run: ``Add-AppxPackage -Path "DarbotMemoryMcp.msix"``

### Option 2: Manual Installation
1. Extract the MSIX package (rename to .zip and extract)
2. Run DarbotMemoryMcp.exe directly from the extracted folder

## Running the Application

The application will start on http://localhost:5093 by default.

Configuration files:
- appsettings.json - Main configuration
- appsettings.Production.json - Production overrides

Conversation files will be stored in: ``./conversations/``

## Health Checks
- Live: http://localhost:5093/health/live
- Ready: http://localhost:5093/health/ready

## API Documentation
- Swagger UI: http://localhost:5093/swagger

## Configuration

Environment variables can be used to override settings:
- ``DARBOT__STORAGE__FILESYSTEM__ROOTPATH`` - Change storage location
- ``ASPNETCORE_URLS`` - Change listening URLs

Example:
```
set DARBOT__STORAGE__FILESYSTEM__ROOTPATH=C:\DarbotMemory
DarbotMemoryMcp.exe
```

For more information, see: https://github.com/darbotlabs/darbot-memory-mcp
"@

$instructionsContent | Out-File -FilePath "$outputDir/INSTALLATION.md" -Encoding utf8

Write-Host "📋 Installation instructions created" -ForegroundColor Green

# Final package structure
Write-Host "`n📁 Package structure:" -ForegroundColor Yellow
Get-ChildItem $outputDir -Recurse | ForEach-Object {
    $indent = "  " * ($_.FullName.Substring($outputDir.Length).Split([IO.Path]::DirectorySeparatorChar).Length - 2)
    Write-Host "$indent$($_.Name)" -ForegroundColor Gray
}

Write-Host "`n🎉 MSIX package preparation completed!" -ForegroundColor Green
Write-Host "Package location: $outputDir" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Review the package contents in: $outputDir" -ForegroundColor White
Write-Host "  2. Test the executable: $outputDir/app/DarbotMemoryMcp.exe" -ForegroundColor White
Write-Host "  3. Create MSIX package using Windows SDK tools or Visual Studio" -ForegroundColor White
Write-Host "  4. See INSTALLATION.md for deployment instructions" -ForegroundColor White
Write-Host ""
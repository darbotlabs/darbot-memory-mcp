# NuGet Package Publishing Guide

## 📦 Generated NuGet Packages

The Darbot Memory MCP solution has been configured to produce the following NuGet packages:

### 1. **Darbot.Memory.Mcp.Core** (Library Package)
- **Description**: Core business logic and services for conversational memory persistence
- **Package**: `Darbot.Memory.Mcp.Core.1.0.0.nupkg`
- **Symbols**: `Darbot.Memory.Mcp.Core.1.0.0.snupkg`
- **Size**: ~108 KB

### 2. **Darbot.Memory.Mcp.Storage** (Library Package)
- **Description**: Storage provider implementations (FileSystem, Git, Azure Blob)
- **Package**: `Darbot.Memory.Mcp.Storage.1.0.0.nupkg`
- **Symbols**: `Darbot.Memory.Mcp.Storage.1.0.0.snupkg`
- **Size**: ~40 KB

### 3. **Darbot.Memory.Mcp.Server** (Global Tool)
- **Description**: Complete MCP server as installable .NET global tool
- **Package**: `Darbot.Memory.Mcp.Server.1.0.0.nupkg`
- **Symbols**: `Darbot.Memory.Mcp.Server.1.0.0.snupkg`  
- **Size**: ~15 MB
- **Tool Command**: `darbot-memory-mcp`

---

## 🚀 Installation Options

### Option 1: Install as Global Tool (Recommended)
```bash
# Install from NuGet.org (when published)
dotnet tool install -g Darbot.Memory.Mcp.Server

# Install from local packages (for testing)
dotnet tool install -g --add-source "./nupkg" Darbot.Memory.Mcp.Server

# Run the server
darbot-memory-mcp
```

### Option 2: Use as Library Dependencies
```xml
<PackageReference Include="Darbot.Memory.Mcp.Core" Version="1.0.0" />
<PackageReference Include="Darbot.Memory.Mcp.Storage" Version="1.0.0" />
```

---

## 📤 Publishing to NuGet.org

### Prerequisites
1. **NuGet.org Account**: Create account at https://nuget.org
2. **API Key**: Generate API key in your NuGet.org profile
3. **Configure API Key**:
   ```bash
   dotnet nuget setapikey YOUR_API_KEY --source https://api.nuget.org/v3/index.json
   ```

### Publishing Commands
```bash
# Publish Core library
dotnet nuget push ./nupkg/Darbot.Memory.Mcp.Core.1.0.0.nupkg --source https://api.nuget.org/v3/index.json
dotnet nuget push ./nupkg/Darbot.Memory.Mcp.Core.1.0.0.snupkg --source https://api.nuget.org/v3/index.json

# Publish Storage library
dotnet nuget push ./nupkg/Darbot.Memory.Mcp.Storage.1.0.0.nupkg --source https://api.nuget.org/v3/index.json
dotnet nuget push ./nupkg/Darbot.Memory.Mcp.Storage.1.0.0.snupkg --source https://api.nuget.org/v3/index.json

# Publish Server tool
dotnet nuget push ./nupkg/Darbot.Memory.Mcp.Server.1.0.0.nupkg --source https://api.nuget.org/v3/index.json
dotnet nuget push ./nupkg/Darbot.Memory.Mcp.Server.1.0.0.snupkg --source https://api.nuget.org/v3/index.json
```

### Automated Publishing Script
```powershell
# publish-nuget.ps1
$packages = @(
    "Darbot.Memory.Mcp.Core.1.0.0.nupkg",
    "Darbot.Memory.Mcp.Storage.1.0.0.nupkg", 
    "Darbot.Memory.Mcp.Server.1.0.0.nupkg"
)

foreach ($package in $packages) {
    Write-Host "Publishing $package..." -ForegroundColor Green
    dotnet nuget push "./nupkg/$package" --source https://api.nuget.org/v3/index.json
    
    # Also push symbol package
    $symbolPackage = $package.Replace(".nupkg", ".snupkg")
    dotnet nuget push "./nupkg/$symbolPackage" --source https://api.nuget.org/v3/index.json
}
```

---

## 🔄 Version Management

The project uses **Nerdbank.GitVersioning** for automatic version management:

- **Current Version**: `1.0-preview`
- **Release Branches**: `main`, `release/*`
- **Pre-release Suffix**: Automatically added for non-release branches

### Updating Version
Edit `version.json`:
```json
{
  "version": "1.1-preview"
}
```

---

## 📋 Package Metadata

All packages include comprehensive metadata:
- **Authors**: Darbot Labs
- **License**: MIT
- **Tags**: mcp, model-context-protocol, conversation, memory, ai, enterprise
- **Repository**: https://github.com/darbotlabs/darbot-memory-mcp
- **Documentation**: Included README and XML docs

---

## ✅ Post-Publishing Verification

After publishing to NuGet.org:

1. **Search Package**: https://www.nuget.org/packages/Darbot.Memory.Mcp.Server
2. **Install Globally**: `dotnet tool install -g Darbot.Memory.Mcp.Server`
3. **Test Installation**: `darbot-memory-mcp --help`
4. **Verify in GitHub Copilot CLI**: MCP integration should work seamlessly

---

## 🎯 GitHub Copilot CLI Integration

Once published to NuGet, users can install and configure the tool:

```bash
# Install the tool globally
dotnet tool install -g Darbot.Memory.Mcp.Server

# Update MCP configuration in ~/.copilot/mcp-config.json
{
  "mcpServers": {
    "darbot-memory-mcp": {
      "type": "local", 
      "command": "darbot-memory-mcp",
      "args": [],
      "env": {
        "DARBOT__STORAGE__FILESYSTEM__ROOTPATH": "~/.copilot/conversations"
      }
    }
  }
}
```

---

**✨ Ready for enterprise-grade conversational memory in GitHub Copilot CLI!**
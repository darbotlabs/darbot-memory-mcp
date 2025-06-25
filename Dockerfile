# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /source

# Copy solution and project files
COPY *.sln .
COPY src/Darbot.Memory.Mcp.Api/*.csproj ./src/Darbot.Memory.Mcp.Api/
COPY src/Darbot.Memory.Mcp.Core/*.csproj ./src/Darbot.Memory.Mcp.Core/
COPY src/Darbot.Memory.Mcp.Storage/*.csproj ./src/Darbot.Memory.Mcp.Storage/
COPY src/Darbot.Memory.Mcp.Tests/*.csproj ./src/Darbot.Memory.Mcp.Tests/

# Restore dependencies
RUN dotnet restore

# Copy source code
COPY src/ ./src/

# Build and test
RUN dotnet build --configuration Release --no-restore
RUN dotnet test --configuration Release --no-build --verbosity minimal

# Publish API project
RUN dotnet publish src/Darbot.Memory.Mcp.Api/Darbot.Memory.Mcp.Api.csproj \
    --configuration Release \
    --no-build \
    --output /app \
    --runtime linux-x64 \
    --self-contained false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Create app user
RUN addgroup --system --gid 1001 appgroup && \
    adduser --system --uid 1001 --ingroup appgroup appuser

WORKDIR /app

# Copy published application
COPY --from=build /app .

# Create data directory and set permissions
RUN mkdir -p /data && \
    chown -R appuser:appgroup /app /data

# Switch to non-root user
USER appuser

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DARBOT__STORAGE__FILESYSTEM__ROOTPATH=/data

# Expose port
EXPOSE 80

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:80/health/live || exit 1

# Start the application
ENTRYPOINT ["dotnet", "Darbot.Memory.Mcp.Api.dll"]
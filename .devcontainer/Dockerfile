FROM mcr.microsoft.com/devcontainers/base:ubuntu-22.04

# Install .NET 8.0
RUN apt-get update && apt-get install -y wget
RUN wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
RUN dpkg -i packages-microsoft-prod.deb
RUN apt-get update && apt-get install -y dotnet-sdk-8.0

# Install PowerShell
RUN apt-get install -y powershell

# Install additional tools
RUN apt-get install -y \
    git \
    curl \
    jq \
    tree \
    nano \
    && rm -rf /var/lib/apt/lists/*

# Create vscode user
RUN useradd -m -s /bin/bash vscode
USER vscode

# Install dotnet tools
RUN dotnet tool install --global nbgv
RUN dotnet tool install --global dotnet-format
RUN dotnet tool install --global dotnet-outdated-tool

# Add dotnet tools to PATH
ENV PATH="/home/vscode/.dotnet/tools:${PATH}"
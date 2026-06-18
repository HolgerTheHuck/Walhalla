# Multi-Stage Build for Walhalla.VectorStore.Api
# Targets .NET 10.0 Preview

# ---- Build Stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY VectorStore.sln ./
COPY Walhalla.VectorStore.Api/Walhalla.VectorStore.Api.csproj Walhalla.VectorStore.Api/
COPY Walhalla.VectorStore/Walhalla.VectorStore.csproj Walhalla.VectorStore/
COPY Walhalla.Storage.Blobs/Walhalla.Storage.Blobs.csproj Walhalla.Storage.Blobs/
COPY Walhalla.Storage/Walhalla.Storage.csproj Walhalla.Storage/
COPY Walhalla.VectorStore.Client/Walhalla.VectorStore.Client.csproj Walhalla.VectorStore.Client/
COPY Walhalla.VectorStore.Microsoft.Extensions.VectorData/Walhalla.VectorStore.Microsoft.Extensions.VectorData.csproj Walhalla.VectorStore.Microsoft.Extensions.VectorData/

# Restore dependencies
RUN dotnet restore Walhalla.VectorStore.Api/Walhalla.VectorStore.Api.csproj

# Copy everything else
COPY . .

# Build and publish
RUN dotnet publish Walhalla.VectorStore.Api/Walhalla.VectorStore.Api.csproj \
    -c Release \
    -o /app/publish

# ---- Runtime Stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Install wget for health checks
RUN apt-get update && apt-get install -y wget && rm -rf /var/lib/apt/lists/*

# Create non-root user for security
RUN useradd -m -s /bin/sh walhalla

# Copy published app
COPY --from=build /app/publish .

# Data volume
RUN mkdir -p /data && chown walhalla:walhalla /data
VOLUME ["/data"]
ENV VectorStore__Path=/data
ENV ASPNETCORE_URLS=http://+:5000

# Switch to non-root
USER walhalla

EXPOSE 5000

# Health check (will be available after Phase 2)
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -qO- --tries=1 --header="X-API-Key: walhalla-dev-key" http://localhost:5000/api/stats > /dev/null || exit 1

ENTRYPOINT ["dotnet", "Walhalla.VectorStore.Api.dll"]

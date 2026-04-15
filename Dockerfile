# ── build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Restore is cached separately from the rest of the source
COPY DbBackupAgent/DbBackupAgent.csproj ./DbBackupAgent/
RUN dotnet restore ./DbBackupAgent/DbBackupAgent.csproj

COPY DbBackupAgent/ ./DbBackupAgent/
RUN dotnet publish ./DbBackupAgent/DbBackupAgent.csproj \
      -c Release \
      -o /app/publish \
      --no-restore

# ── runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base

# Install pg_dump (postgresql-client) for Postgres backups.
# For MSSQL backups, add the Microsoft mssql-tools package:
#   https://learn.microsoft.com/sql/linux/sql-server-linux-setup-tools
RUN apt-get update && \
    apt-get install -y --no-install-recommends postgresql-client && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Default output directory; override via BackupSettings:OutputPath or a bind-mount
VOLUME ["/backups"]

ENTRYPOINT ["dotnet", "DbBackupAgent.dll"]

# ── build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Restore is cached separately from the rest of the source
COPY DbBackupAgent/DbBackupAgent.csproj ./DbBackupAgent/
RUN dotnet restore ./DbBackupAgent/DbBackupAgent.csproj

COPY DbBackupAgent/ ./DbBackupAgent/
COPY appsettings.json ./
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
    apt-get install -y --no-install-recommends curl ca-certificates gnupg lsb-release && \
    install -d /usr/share/postgresql-common/pgdg && \
    curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
      -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc && \
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" \
      > /etc/apt/sources.list.d/pgdg.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends postgresql-client-17 && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Default output directory; override via BackupSettings:OutputPath or a bind-mount
VOLUME ["/backups"]

ENTRYPOINT ["dotnet", "DbBackupAgent.dll"]

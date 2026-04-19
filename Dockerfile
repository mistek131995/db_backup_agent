# ── build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Restore is cached separately from the rest of the source
COPY BackupsterAgent/BackupsterAgent.csproj ./BackupsterAgent/
RUN dotnet restore ./BackupsterAgent/BackupsterAgent.csproj

COPY BackupsterAgent/ ./BackupsterAgent/
COPY appsettings.json ./
RUN dotnet publish ./BackupsterAgent/BackupsterAgent.csproj \
      -c Release \
      -o /app/publish \
      --no-restore

# ── runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl ca-certificates gnupg lsb-release && \
    install -d /usr/share/postgresql-common/pgdg && \
    curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
      -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc && \
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" \
      > /etc/apt/sources.list.d/pgdg.list && \
    curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
      | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg && \
    echo "deb [arch=amd64,arm64 signed-by=/usr/share/keyrings/microsoft.gpg] https://packages.microsoft.com/debian/12/prod bookworm main" \
      > /etc/apt/sources.list.d/mssql-release.list && \
    apt-get update && \
    ACCEPT_EULA=Y apt-get install -y --no-install-recommends \
      postgresql-client-17 \
      default-mysql-client \
      mssql-tools18 && \
    rm -rf /var/lib/apt/lists/*

ENV PATH="/opt/mssql-tools18/bin:${PATH}"

WORKDIR /app
COPY --from=build /app/publish .

# Default output directory; override via BackupSettings:OutputPath or a bind-mount
VOLUME ["/backups"]

ENTRYPOINT ["dotnet", "BackupsterAgent.dll"]

# DbBackupAgent

.NET 10 Worker Service для автоматического резервного копирования баз данных.

## Что делает

Для каждой базы данных из конфига запускает pipeline из 6 шагов:

```
Dump → Encrypt → Upload → Cleanup → File Snapshots → Report
```

1. **Dump** — вызывает `pg_dump` или `sqlcmd`, сохраняет файл на диск.
2. **Encrypt** — AES-256-CBC, IV (16 байт) prepended, добавляет суффикс `.enc`.
3. **Upload** — загружает зашифрованный файл в S3 или SFTP.
4. **Cleanup** — удаляет оба локальных файла (дамп + зашифрованный), всегда, даже при ошибке.
5. **File Snapshots** — шифрует и загружает произвольные файлы из `FilePaths` (независимо от результата шага 1–4).
6. **Report** — отправляет отчёт на DbBackupDashboard, всегда, при успехе и при ошибке.

Расписание запусков получает из Dashboard (cron, опрос каждые 5 минут).

Поддерживаемые БД: **PostgreSQL**, **MSSQL**.  
Поддерживаемые хранилища: **S3-совместимые** (MinIO, Yandex Object Storage, AWS S3), **SFTP**.

---

## Требования

- .NET 10 SDK
- `pg_dump` в `PATH` — для PostgreSQL
- `sqlcmd` в `PATH` — для MSSQL
- Зарегистрированный агент в DbBackupDashboard (нужен токен)

---

## Быстрый старт

```bash
cd DbBackupAgent
# Заполнить appsettings.json (см. ниже)
dotnet run --project DbBackupAgent/DbBackupAgent.csproj
```

---

## Конфигурация

Все настройки в `appsettings.json`. Любой параметр можно переопределить переменной окружения.

### Базы данных

```json
"Databases": [
  {
    "DatabaseType": "Postgres",
    "Host": "localhost",
    "Port": 5432,
    "Database": "mydb",
    "Username": "user",
    "Password": "secret",
    "OutputPath": "/tmp/backups",
    "FilePaths": []
  },
  {
    "DatabaseType": "Mssql",
    "Host": "localhost",
    "Port": 1433,
    "Database": "mydb2",
    "Username": "sa",
    "Password": "secret",
    "OutputPath": "/tmp/backups",
    "FilePaths": ["/etc/myapp/config.yml", "/var/data/certs"]
  }
]
```

- `OutputPath` — папка для временных файлов дампа. Файлы удаляются после загрузки.
- `FilePaths` — список путей к файлам или директориям, которые будут зашифрованы и загружены как файловые снимки. Директории обходятся рекурсивно. Поле необязательное, по умолчанию пустое.

### Шифрование

```json
"EncryptionSettings": {
  "Key": "<base64 от 32 байт>"
}
```

Сгенерировать ключ:

```bash
# Linux / macOS
openssl rand -base64 32

# PowerShell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

### Хранилище — S3

```json
"UploadSettings": { "Provider": "S3" },
"S3Settings": {
  "EndpointUrl": "https://storage.yandexcloud.net",
  "AccessKey": "...",
  "SecretKey": "...",
  "BucketName": "my-bucket",
  "Region": "us-east-1"
}
```

> Для MinIO и Yandex Object Storage включён `ForcePathStyle` — ничего дополнительно настраивать не нужно.

### Хранилище — SFTP

```json
"UploadSettings": { "Provider": "Sftp" },
"SftpSettings": {
  "Host": "backup.example.com",
  "Port": 22,
  "Username": "backupuser",
  "Password": "",
  "PrivateKeyPath": "/root/.ssh/id_rsa",
  "PrivateKeyPassphrase": "",
  "RemotePath": "/var/backups"
}
```

Поддерживается аутентификация по паролю и по приватному ключу. Удалённые директории создаются автоматически.

### Подключение к Dashboard

```json
"AgentSettings": {
  "DashboardUrl": "http://your-server:8080",
  "Token": "<токен агента из Dashboard>"
}
```

Токен передаётся через заголовок `X-Agent-Token`. Расписание опрашивается каждые 5 минут.

---

## Структура файлов в хранилище

```
{database}/{yyyy-MM-dd}/{filename}.sql.gz.enc    ← PostgreSQL дамп
{database}/{yyyy-MM-dd}/{filename}.bak.enc       ← MSSQL дамп
{database}/files/{yyyy-MM-dd}/{filename}.enc     ← файловые снимки (FilePaths)
```

---

## Поведение при ошибках

- Ошибка одной БД не останавливает обработку остальных.
- Ошибка отдельного файла в `FilePaths` не останавливает обработку остальных файлов.
- Отчёт отправляется всегда — и при успехе, и при ошибке.
- Временные файлы удаляются даже если pipeline упал.
- `ReportService` и `ScheduleService` делают до 3 повторных попыток (1 с → 2 с → 4 с) при недоступности Dashboard.

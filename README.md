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
Проверка cron-расписания — каждые 30 секунд.

Поддерживаемые БД: **PostgreSQL**, **MSSQL**.  
Поддерживаемые хранилища: **S3-совместимые** (MinIO, Yandex Object Storage, AWS S3, Cloudflare R2), **SFTP**.

---

## Требования

- .NET 10 Runtime (или SDK для сборки из исходников)
- `pg_dump` в `PATH` — для PostgreSQL
- `sqlcmd` в `PATH` — для MSSQL
- Зарегистрированный агент в DbBackupDashboard (нужен токен)

---

## Запуск

### Docker

```bash
docker run -d --name dbbackup-agent \
  -e AgentSettings__Token=<токен> \
  -e AgentSettings__DashboardUrl=<url дашборда> \
  -v /root/dbbackup-agent:/app/config \
  ghcr.io/mistek131995/db_backup_agent:latest
```

При первом запуске агент создаст шаблон `/app/config/appsettings.json`. Заполните его и перезапустите контейнер:

```bash
docker restart dbbackup-agent
```

### Linux (systemd)

```bash
sudo mkdir -p /opt/dbbackup-agent
# скопируйте опубликованные файлы в /opt/dbbackup-agent

sudo tee /etc/systemd/system/dbbackup-agent.service <<EOF
[Unit]
Description=DbBackup Agent
After=network.target

[Service]
WorkingDirectory=/opt/dbbackup-agent
ExecStart=/opt/dbbackup-agent/DbBackupAgent
Environment=AgentSettings__Token=<токен>
Environment=AgentSettings__DashboardUrl=<url дашборда>
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now dbbackup-agent
```

При первом запуске агент создаст шаблон `/app/config/appsettings.json`. Заполните его и перезапустите службу:

```bash
sudo systemctl restart dbbackup-agent
```

### Windows (служба)

```powershell
# Распакуйте опубликованные файлы в C:\Services\DbBackupAgent

sc.exe create DbBackupAgent binPath="C:\Services\DbBackupAgent\DbBackupAgent.exe"

# Задайте переменные окружения
reg add "HKLM\SYSTEM\CurrentControlSet\Services\DbBackupAgent\Environment" ^
  /v AgentSettings__Token /t REG_SZ /d "<токен>"
reg add "HKLM\SYSTEM\CurrentControlSet\Services\DbBackupAgent\Environment" ^
  /v AgentSettings__DashboardUrl /t REG_SZ /d "<url дашборда>"

sc.exe start DbBackupAgent
```

При первом запуске агент создаст шаблон `C:\Services\DbBackupAgent\config\appsettings.json`. Заполните его и перезапустите службу:

```powershell
sc.exe stop DbBackupAgent
sc.exe start DbBackupAgent
```

### Для разработки

```bash
cd DbBackupAgent
dotnet run --project DbBackupAgent/DbBackupAgent.csproj
```

---

## Поведение при пустом конфиге

Агент **не падает** если конфигурация не заполнена:

- Нет ключа шифрования — логирует warning, пропускает бэкапы
- Нет настроек S3 — логирует warning, клиент не создаётся до первого вызова
- Нет баз данных — логирует warning, пропускает бэкапы
- Token и DashboardUrl пустые — расписание не загружается, агент простаивает

Заполните `appsettings.json` и перезапустите — агент начнёт работать.

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

Token и DashboardUrl передаются через переменные окружения (не в `appsettings.json`):

```bash
AgentSettings__Token=<токен агента из Dashboard>
AgentSettings__DashboardUrl=http://your-server:8080
```

Токен передаётся на сервер через заголовок `X-Agent-Token`. Расписание опрашивается каждые 5 минут.

### Путь к конфигу

По умолчанию агент ищет `appsettings.json` в:
- **Docker / Linux:** `/app/config/`
- **Windows:** `{директория exe}\config\`

Переопределяется через переменную окружения `CONFIG_PATH`.

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

---

## Heartbeat

Агент обновляет статус "в сети" на дашборде при каждом запросе расписания (каждые 5 минут). Отдельного heartbeat-эндпоинта нет — используется `GET /api/v1/agent/schedule`.

---

## Версионирование образов и релизов

Релизы публикуются по git-тегам `v*`. Суффикс тега определяет канал:

| Суффикс | Docker-теги в GHCR | GitHub Release |
|---|---|---|
| без суффикса | сам тег + `latest` | стабильный |
| `b` (бета) | сам тег + `latest` | prerelease |
| `e` (экспериментальный) | только сам тег | prerelease |

Пока стабильных релизов нет, бета-версии (`b`) публикуются под `latest` — именно их получают пользователи, запустившие образ без явного тега. Экспериментальные (`e`) ставятся только по полному тегу и не затрагивают `latest`.

Чтобы закрепиться на конкретной версии — укажите полный тег в `docker run` вместо `latest`.

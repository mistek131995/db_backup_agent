# BackupsterAgent

- **Сайт:** [backupster.io](https://backupster.io/)
- **Сетевая безопасность:** [NETWORK.md](NETWORK.md)

---

.NET 10 Worker Service для автоматического резервного копирования баз данных.

Панель управления агентом — **[backupster.io](https://backupster.io/)**. Там регистрируется агент, выдаётся токен, настраивается расписание и смотрится история бэкапов.

## Что делает

Агент выполняет два независимых пайплайна на одном хосте: резервное копирование по расписанию и восстановление по команде с дашборда.

### Backup (по cron)

```
Dump → Encrypt → Upload → Cleanup → File Backup (только S3) → Report
```

1. **Dump** — вызывает `pg_dump` или `sqlcmd`, сохраняет файл на диск.
2. **Encrypt** — AES-256-GCM, дамп режется на фреймы по 1 МиБ (каждый со своим nonce и tag). Файл начинается с header `BK02`; AAD каждого фрейма — его порядковый номер (uint32 big-endian), что делает перестановку фреймов или склейку с другим дампом невалидной. Чанки файлового бэкапа шифруются с AAD = sha256 плейнтекста, манифест — с AAD = UTF-8 своего object key. Добавляется суффикс `.enc`.
3. **Upload** — загружает зашифрованный файл в S3 или SFTP.
4. **Cleanup** — удаляет оба локальных файла (дамп + зашифрованный), всегда, даже при ошибке.
5. **File Backup** — если `FilePaths` непуст: режет каждый файл на content-defined chunks (FastCDC, ~4 МиБ), считает sha256 и грузит только новые куски в общий пул `chunks/{sha256}` (дедупликация через HEAD). Зашифрованный манифест (список файлов + хэши) кладётся рядом с дампом в `manifest.json.enc`. **Работает только с S3** — при SFTP пропускается с warning. Ошибка на этом этапе не валит отчёт о дампе.
6. **Report** — отправляет отчёт на DbBackupDashboard о статусе дампа и (если был) файлового этапа.

Расписание запусков получает из Dashboard (cron, опрос каждые 5 минут).
Проверка cron-расписания — каждые 30 секунд.

### Сборщик мусора для чанков (S3)

Файловый бэкап дедуплицирует куски в общий пул `chunks/{sha256}` — один и тот же чанк, встретившийся в нескольких бэкапах, лежит в хранилище в одном экземпляре. Когда какой-то бэкап (манифест) удаляют, его чанки могут оказаться осиротевшими, если они не используются ни одним другим бэкапом.

Агент периодически их находит и удаляет:

1. По таймеру (по умолчанию раз в 24 часа) сканирует каждое S3-хранилище.
2. Читает все `*/manifest.json.enc`, расшифровывает их, собирает sha256 всех упомянутых чанков в одно множество.
3. Листит `chunks/` и удаляет каждый объект, которого нет в множестве **и** который старше grace-окна (по умолчанию 24 часа — защита от гонки, когда параллельный бэкап уже залил чанк, но ещё не обновил манифест).

Особенности:

- **Только S3.** У SFTP файлового бэкапа и чанков нет, удалять нечего.
- **Первый запуск** — через 10 минут после старта агента, чтобы не конкурировать с начальной синхронизацией.
- **Не пересекается с backup/restore.** На время sweep агент берёт общий lock — если идёт бэкап или восстановление, GC ждёт.
- **Дамп и manifest GC не трогает** — подчищается только пул `chunks/`. Удаление устаревших дамп-папок по retention — отдельная задача (пока не реализована).
- **Ошибка при чтении/дешифровке любого манифеста прерывает sweep этого хранилища.** Иначе можно удалить чанки, которые на самом деле ещё нужны.

### Restore (по задачам с дашборда)

```
Poll task → Download → Decrypt → Restore DB → Restore Files → Report
```

1. **Long-poll** — раз в 30 секунд запрашивает `GET /api/v1/agent/restore-task`. При отсутствии задачи — ждёт дальше; при получении — сразу исполняет и тут же опрашивает снова.
2. **Database restore** — проверяет права target-подключения одним SQL-запросом до скачивания дампа → скачивает и расшифровывает → пересоздаёт target БД (DROP+CREATE) → `psql -f` (Postgres) или `RESTORE DATABASE ... WITH MOVE` (MSSQL, автоматический ремап логических имён) → cleanup временных файлов.
3. **File restore** — если в бэкапе были файлы (`ManifestKey`), скачивает зашифрованный манифест, поштучно собирает каждый файл из чанков, атомарно переименовывает `.restore-tmp` → target. Падение одного файла не валит задачу — статус `partial` с подробностями.
4. **Report** — `PATCH /api/v1/agent/restore-task/{id}` с финальным статусом (`success` / `failed` / `partial`).

**Backup и restore на одном агенте не идут параллельно** — координация через общий lock. Запуск по расписанию ждёт, если идёт восстановление, и наоборот.

Поддерживаемые БД: **PostgreSQL**, **MSSQL**.  
Поддерживаемые хранилища: **S3-совместимые** (MinIO, Yandex Object Storage, AWS S3, Cloudflare R2), **SFTP**.

---

## Требования

- .NET 10 Runtime (или SDK для сборки из исходников)
- `pg_dump` в `PATH` — для PostgreSQL
- `sqlcmd` в `PATH` — для MSSQL
- Зарегистрированный агент на [backupster.io](https://backupster.io/) (нужен токен)

---

## Запуск

### Docker

```bash
docker run -d --name backupster-agent \
  -e AgentSettings__Token=<токен> \
  -e AgentSettings__DashboardUrl=<url дашборда> \
  -v /root/backupster-agent:/app/config \
  ghcr.io/mistek131995/backupster-agent:latest
```

Если планируете использовать файловый бэкап (`FilePaths`), смонтируйте исходные директории в контейнер отдельными томами и пропишите их контейнерные пути в `FilePaths`:

```bash
docker run -d --name backupster-agent \
  -e AgentSettings__Token=<токен> \
  -e AgentSettings__DashboardUrl=<url дашборда> \
  -v /root/backupster-agent:/app/config \
  -v /var/app/uploads:/app/data/uploads \
  -v /etc/app:/app/data/config \
  ghcr.io/mistek131995/backupster-agent:latest
```

В `appsettings.json`: `"FilePaths": ["/app/data/uploads", "/app/data/config"]`.

При первом запуске агент создаст шаблон `/app/config/appsettings.json`. Заполните его и перезапустите контейнер:

```bash
docker restart backupster-agent
```

### Linux (systemd)

```bash
sudo mkdir -p /opt/backupster-agent
# скопируйте опубликованные файлы в /opt/backupster-agent

sudo tee /etc/systemd/system/backupster-agent.service <<EOF
[Unit]
Description=Backupster Agent
After=network.target

[Service]
WorkingDirectory=/opt/backupster-agent
ExecStart=/opt/backupster-agent/BackupsterAgent
Environment=AgentSettings__Token=<токен>
Environment=AgentSettings__DashboardUrl=<url дашборда>
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now backupster-agent
```

При первом запуске агент создаст шаблон `/app/config/appsettings.json`. Заполните его и перезапустите службу:

```bash
sudo systemctl restart backupster-agent
```

### Windows (служба)

```powershell
# Распакуйте опубликованные файлы в C:\Services\BackupsterAgent

sc.exe create BackupsterAgent binPath="C:\Services\BackupsterAgent\BackupsterAgent.exe"

# Задайте переменные окружения
reg add "HKLM\SYSTEM\CurrentControlSet\Services\BackupsterAgent\Environment" ^
  /v AgentSettings__Token /t REG_SZ /d "<токен>"
reg add "HKLM\SYSTEM\CurrentControlSet\Services\BackupsterAgent\Environment" ^
  /v AgentSettings__DashboardUrl /t REG_SZ /d "<url дашборда>"

sc.exe start BackupsterAgent
```

При первом запуске агент создаст шаблон `C:\Services\BackupsterAgent\config\appsettings.json`. Заполните его и перезапустите службу:

```powershell
sc.exe stop BackupsterAgent
sc.exe start BackupsterAgent
```

### Для разработки

```bash
cd BackupsterAgent
dotnet run --project BackupsterAgent/BackupsterAgent.csproj
```

---

## Поведение при пустом конфиге

Агент **не падает** если конфигурация не заполнена:

- Нет ключа шифрования — логирует warning, пропускает бэкапы
- Нет настроек хранилища у storage — при первом обращении клиент упадёт с явной ошибкой; другие БД с корректным storage продолжат работать
- Нет баз данных — логирует warning, пропускает бэкапы
- Token и DashboardUrl пустые — расписание не загружается, агент простаивает

Заполните `appsettings.json` и перезапустите — агент начнёт работать.

---

## Конфигурация

Все настройки в `appsettings.json`. Любой параметр можно переопределить переменной окружения.

### Подключения, хранилища и базы данных

Конфиг разбит на три списка:

- `Connections[]` — реквизиты серверов БД (хост, логин, пароль, тип).
- `Storages[]` — хранилища для бэкапов (S3 или SFTP); у каждого уникальное имя и собственный набор настроек.
- `Databases[]` — список баз; каждая ссылается на подключение и на хранилище по имени.

Такое разделение позволяет не дублировать реквизиты сервера для нескольких БД и класть разные БД в разные бакеты/хранилища.

```json
"Connections": [
  {
    "Name": "main-pg",
    "DatabaseType": "Postgres",
    "Host": "localhost",
    "Port": 5432,
    "Username": "user",
    "Password": "secret"
  },
  {
    "Name": "reporting-mssql",
    "DatabaseType": "Mssql",
    "Host": "localhost",
    "Port": 1433,
    "Username": "sa",
    "Password": "secret",
    "SharedBackupPath": "/var/opt/mssql/backups",
    "AgentBackupPath": null
  }
],
"Storages": [
  {
    "Name": "prod-s3",
    "Provider": "S3",
    "S3": {
      "EndpointUrl": "https://storage.yandexcloud.net",
      "AccessKey": "...",
      "SecretKey": "...",
      "BucketName": "prod-backups",
      "Region": "us-east-1"
    }
  },
  {
    "Name": "archive-sftp",
    "Provider": "Sftp",
    "Sftp": {
      "Host": "backup.example.com",
      "Port": 22,
      "Username": "backupuser",
      "PrivateKeyPath": "/root/.ssh/id_rsa",
      "RemotePath": "/var/backups"
    }
  }
],
"Databases": [
  {
    "ConnectionName": "main-pg",
    "StorageName": "prod-s3",
    "Database": "mydb",
    "OutputPath": "/tmp/backups",
    "FilePaths": []
  },
  {
    "ConnectionName": "reporting-mssql",
    "StorageName": "archive-sftp",
    "Database": "mydb2",
    "OutputPath": "/tmp/backups",
    "FilePaths": ["/etc/myapp/config.yml", "/var/data/certs"]
  }
]
```

- `Name` подключения и `Name` хранилища должны быть уникальны в пределах своих списков.
- `ConnectionName` и `StorageName` у БД обязаны ссылаться на существующие записи — иначе эта БД будет пропущена с ошибкой в логе, остальные продолжат работать.
- `OutputPath` — папка для временных файлов дампа. Файлы удаляются после загрузки.
- `FilePaths` — список путей к файлам или директориям для файлового бэкапа. Директории обходятся рекурсивно. Файлы режутся на content-defined chunks (FastCDC, ~4 МиБ) и дедуплицируются внутри одного хранилища. Работает только на S3-хранилищах. Поле необязательное.
- `SharedBackupPath` / `AgentBackupPath` — **только для MSSQL**, подробнее — в разделе [MSSQL: каталог `.bak`](#mssql-каталог-bak). Для Postgres и MySQL поля не используются.

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

### Хранилища — настройки провайдеров

Каждая запись в `Storages[]` имеет поле `Provider` (`S3` или `Sftp`) и соответствующий вложенный блок (`S3` либо `Sftp`). Лишний блок для выбранного провайдера игнорируется.

**S3-хранилище:**

```json
{
  "Name": "prod-s3",
  "Provider": "S3",
  "S3": {
    "EndpointUrl": "https://storage.yandexcloud.net",
    "AccessKey": "...",
    "SecretKey": "...",
    "BucketName": "my-bucket",
    "Region": "us-east-1"
  }
}
```

> Для MinIO и Yandex Object Storage включён `ForcePathStyle` — ничего дополнительно настраивать не нужно.

> **`Region`** — для AWS S3 ставьте реальный регион бакета (например, `eu-central-1`). Для S3-совместимых хранилищ (MinIO, Yandex Object Storage) значение используется только для подписи запросов и игнорируется сервером — принято указывать `us-east-1`. Для Cloudflare R2 официальное значение — `auto`.

**SFTP-хранилище:**

```json
{
  "Name": "archive-sftp",
  "Provider": "Sftp",
  "Sftp": {
    "Host": "backup.example.com",
    "Port": 22,
    "Username": "backupuser",
    "Password": "",
    "PrivateKeyPath": "/root/.ssh/id_rsa",
    "PrivateKeyPassphrase": "",
    "RemotePath": "/var/backups",
    "HostKeyFingerprint": "SHA256:abcDEF123..."
  }
}
```

Поддерживается аутентификация по паролю и по приватному ключу. Удалённые директории создаются автоматически.

> **`HostKeyFingerprint`** — опциональный отпечаток публичного ключа SFTP-сервера в формате `SHA256:<base64 без padding>` (совпадает с тем, что печатает `ssh-keyscan -t rsa host | ssh-keygen -lf -`).
> - Задан → несовпадающий ключ сервера отвергается, агент пишет error «possible MITM» и соединение обрывается.
> - Не задан → агент один раз за время жизни процесса пишет warning с актуальным отпечатком, чтобы вы могли скопировать его в конфиг. Без отпечатка защиты от MITM нет — для prod обязательно задайте.
>
> Как получить отпечаток:
> ```bash
> ssh-keyscan -t rsa backup.example.com 2>/dev/null | ssh-keygen -lf -
> # 256 SHA256:abcDEF123...   backup.example.com (RSA)
> ```

> **Файловый бэкап (`FilePaths`) не работает на SFTP-хранилищах** — у SFTP нет дешёвого `HEAD` для дедупликации кусков. При непустом `FilePaths` для базы, смотрящей на SFTP-хранилище, дамп загрузится, файлы будут пропущены с warning.

### Подключение к Dashboard

Token и DashboardUrl передаются через переменные окружения (не в `appsettings.json`):

```bash
AgentSettings__Token=<токен агента из Dashboard>
AgentSettings__DashboardUrl=http://your-server:8080
```

Токен передаётся на сервер через заголовок `X-Agent-Token`. Расписание опрашивается каждые 5 минут.

> **Сетевая прозрачность.** Полный перечень HTTP-запросов агента к дашборду со схемами тел и перечнем полей — в [`NETWORK.md`](NETWORK.md). Учётные данные БД, ключи шифрования, S3/SFTP-секреты на дашборд **никогда** не уходят.

### Сборщик мусора для чанков (опционально)

```json
"GcSettings": {
  "Enabled": true,
  "IntervalHours": 24,
  "GraceHours": 24
}
```

Всё опционально — блок в шаблон `appsettings.json` не попадает, по умолчанию GC включён с параметрами 24/24.

- **`Enabled`** — выключает worker целиком. `false` если хотите управлять очисткой вручную или делать её внешним инструментом.
- **`IntervalHours`** — как часто запускать sweep. Минимум 1 час (меньшие значения игнорируются). Для больших хранилищ с десятками тысяч объектов разумно поднять до 48–168 — каждый sweep читает и дешифрует все манифесты.
- **`GraceHours`** — не трогать чанки, созданные в последние N часов. Защита от гонки: если параллельный бэкап уже залил чанк, но ещё не записал манифест, чанк временно выглядит осиротевшим. Минимум 0, но ставить ниже длительности самого долгого бэкапа опасно.

Поведение при выключенном шифровании или пустом списке хранилищ — GC молча пропускает запуск (warning в лог).

### Путь к конфигу

По умолчанию агент ищет `appsettings.json` в:
- **Docker / Linux:** `/app/config/`
- **Windows:** `{директория exe}\config\`

Переопределяется через переменную окружения `CONFIG_PATH`.

---

## Структура файлов в хранилище

```
{database}/{yyyy-MM-dd_HH-mm-ss}/
  {database}_{yyyyMMdd_HHmmss}.sql.gz.enc    ← PostgreSQL дамп
  {database}_{yyyyMMdd_HHmmss}.bak.enc       ← MSSQL дамп
  manifest.json.enc                          ← манифест файлового бэкапа (если FilePaths непуст)

chunks/{sha256}                              ← общий пул дедуплицированных чанков (S3 only)
```

---

## MSSQL: каталог .bak

MSSQL делает бэкап на том хосте, где он сам установлен — файл `.bak` появляется на диске этого хоста. MSSQL его создаёт при бэкапе и сам же читает при восстановлении.

Значит, агенту и MSSQL нужен **общий каталог** — одна папка, в которую оба могут писать и из которой оба могут читать.

Проблема в том, что путь к общей папке в файловой системе MSSQL и агента может быть разным. Если агент на той же машине и в той же ОС, что MSSQL — имя папки совпадает. Если агент в контейнере или на другом хосте — он видит её под своим путём (через volume, SMB-шару, NFS и т.п.).

Поэтому в конфиге два поля:

- `SharedBackupPath` — путь к папке так, как её видит **MSSQL**. Этот путь уходит в команду `BACKUP`/`RESTORE`.
- `AgentBackupPath` — путь к **той же самой** папке так, как её видит **агент**. Нужен только если имена отличаются; если совпадают — оставить пустым, агент возьмёт `SharedBackupPath` и для себя.

Важно: это **не две разных папки**, а одна — просто путь разный.

### Примеры конфигурации

| Сценарий | `SharedBackupPath` | `AgentBackupPath` |
|---|---|---|
| Агент и SQL Server на одном хосте (без Docker) | `C:\mssql-backups` (любая локальная папка) | *(не задано)* |
| Docker Compose, общий named volume у обоих контейнеров | `/var/opt/mssql/backups` | *(не задано)* |
| Агент в Docker, SQL Server на хосте; bind-mount одинакового пути | `/var/opt/mssql/backups` | *(не задано)* |
| Windows SQL Server + Linux-контейнер агента, SMB/UNC-шара смонтирована в контейнер | `\\fs\backup` | `/mnt/backup` |
| Оба на разных хостах, NFS-шара смонтирована в оба | `/mnt/nfs/mssql-backups` | *(не задано)* |

### Требования к каталогу

- **Запись** — SQL Server должен иметь право писать в `SharedBackupPath` (для backup), агент должен иметь право читать оттуда (для upload) и писать (для restore — он кладёт туда расшифрованный `.bak` перед `RESTORE`).
- **Чтение** — SQL Server должен иметь право читать из `SharedBackupPath` (для restore).
- **Тот же физический каталог** — это ключевой инвариант. Не два разных (локальная папка агента + отдельная папка SQL Server), а **один каталог**, доступный обеим сторонам под разными путями при необходимости.

### Почему это обязательно для remote MSSQL

Без `SharedBackupPath` на remote-хосте агент попросту не может передать `.bak` между собой и SQL Server'ом:
- На **backup**: SQL Server пишет `.bak` в локальный для себя каталог, который агент не видит и не может забрать.
- На **restore**: агент расшифровывает `.bak` у себя локально, но `RESTORE DATABASE ... FROM DISK = ...` выполняется на SQL Server'e, который не видит файловую систему агента.

При попытке выполнить backup или restore на remote MSSQL без `SharedBackupPath` агент явно падает с ошибкой и подробным сообщением о том, что именно задать в конфиге. Для локального MSSQL (на том же хосте, что и агент) поле не обязательно — fallback на `OutputPath`.

### Способ синхронизации каталога — ответственность пользователя

Агент **не** настраивает volume-ы, SMB/CIFS-монтирование или NFS — он только читает и пишет по указанным путям. Выбирайте то, что лучше всего подходит вашей инфраструктуре:

- **Docker named volume** — проще всего, если оба контейнера (агент и SQL Server) запускаются через один compose-файл или одну Docker-сеть.
- **Bind-mount** — если SQL Server на хосте, агент в контейнере (или наоборот).
- **SMB/CIFS-шара** — для Windows SQL Server с Linux-агентом или кросс-хост сценариев.
- **NFS** — для кросс-хост на Linux.

В любом случае проверьте права на запись/чтение с обеих сторон до попытки бэкапа.

---

## Restore — настройки агента

Восстановление полностью реализовано и запускается из интерфейса дашборда. На стороне агента поведение определяется двумя местами:

### `RestoreSettings` (опционально) — рабочий каталог

```json
"RestoreSettings": {
  "TempPath": "/mnt/restore-temp"
}
```

Куда агент скачивает и расшифровывает дамп перед передачей в `psql`/`sqlcmd`. Каждая задача пишет в свой subdir `{TempPath}/{taskId}/`, cleanup в `try/finally` даже при ошибке.

- **По умолчанию поле не задаётся** — и шаблон `appsettings.json` его не содержит. В этом случае используется хардкод `./temp`, который резолвится относительно директории исполняемого файла (**не** текущего каталога процесса — это важно для Windows-службы, у которой CWD по умолчанию `C:\Windows\System32`).
- **Абсолютный путь** используется как есть. Полезно, если нужно выделить под temp отдельный volume с достаточным местом под многогигабайтные дампы.
- **Для MSSQL** финальный `.bak` кладётся не в `TempPath`, а в `AgentBackupPath ?? SharedBackupPath` — это требование SQL Server (см. раздел про каталог `.bak` выше).

### Требуемые права target-подключения

Агент проверяет права **до** скачивания дампа — чтобы не тратить трафик на задачу, которая в конце упадёт на `permission denied`. Если прав не хватает, в статусе задачи на дашборде будет подробное сообщение с готовой SQL-командой для выдачи прав.

**PostgreSQL** — нужно одно из:
- `rolsuper = true` (superuser), **или**
- `rolcreatedb = true` (для `DROP`/`CREATE DATABASE`) **и** членство в `pg_signal_backend` (для отключения активных сессий).

```sql
ALTER ROLE "restore_user" WITH CREATEDB;
GRANT pg_signal_backend TO "restore_user";
```

**MSSQL** — нужно членство в `sysadmin` или `dbcreator`:

```sql
ALTER SERVER ROLE dbcreator ADD MEMBER [restore_user];
```

### Поведение при восстановлении

- **Всё или ничего.** Если в source-бэкапе были файлы (`ManifestKey != null`), они восстанавливаются всегда вместе с БД. Отдельный restore «только БД» или «только файлы» не поддерживается — привело бы к рассинхрону между таблицами и файловой системой.
- **Target БД перезаписывается.** Перед восстановлением агент делает `DROP DATABASE IF EXISTS` (Postgres) или `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` + `DROP DATABASE` (MSSQL). Активные соединения принудительно закрываются.
- **Файлы перезаписываются.** Каждый файл собирается в `.restore-tmp` и атомарно переименовывается. Ошибка на одном файле → статус задачи `partial`, список упавших файлов — в `ErrorMessage`.
- **Кросс-платформа пока не поддерживается.** Windows-бэкап на Linux-агент (или наоборот) может не заработать из-за особенностей путей и прав. Восстанавливайте на ту же ОС, что делала бэкап.

---

## Поведение при ошибках

- Ошибка одной БД не останавливает обработку остальных.
- Ошибка отдельного файла в `FilePaths` не останавливает обработку остальных файлов.
- Ошибка на файловом этапе не валит отчёт о дампе — он уйдёт с пометкой о файловой ошибке.
- Отчёт о дампе отправляется и при успехе, и при ошибке дампа.
- Временные файлы удаляются даже если pipeline упал.
- `ReportService`, `ScheduleService` и `ConnectionSyncService` делают до 3 повторных попыток (1 с → 2 с → 4 с) при недоступности Dashboard.
- При restore: ошибка на одном файле не валит всю задачу — статус `partial` с подробным списком упавших файлов. Ошибка на этапе БД (permission denied, decrypt failure, невалидный дамп) → статус `failed` с человекочитаемым сообщением, без раскрытия технических exception'ов пользователю. Если агент упадёт между взятием задачи и отчётом — сервер подчистит задачу через 6 часов (sweeper), и её можно будет создать заново.

---

## Heartbeat

Агент обновляет статус "в сети" на дашборде при каждом запросе расписания (каждые 5 минут). Отдельного heartbeat-эндпоинта нет — используется `GET /api/v1/agent/schedule`.

---

## Синхронизация подключений

При старте агент отправляет на дашборд список подключений из `appsettings.json` — **только топологию**: `Name`, `DatabaseType`, `Host`, `Port`. Это нужно, чтобы в будущем на дашборде (например, в диалоге восстановления) можно было выбрать целевое подключение из списка.

**Credentials подключений к БД никогда не покидают хост агента.** `Username` и `Password` из `Connections[]` живут только в `appsettings.json` на машине, где работает агент, и используются только локально при вызове `pg_dump` / `sqlcmd`. Тот же принцип — для ключа шифрования и S3/SFTP credentials.

Sync выполняется один раз на старте. Если дашборд недоступен — агент ретраит с экспоненциальным backoff (до 5 минут между попытками, пока не удастся). После успеха sync-worker останавливается. Чтобы повторить синхронизацию (например, после добавления нового подключения в `appsettings.json`), перезапустите агент.

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

# Конфигурация

Все настройки в `appsettings.json`. Любой параметр можно переопределить переменной окружения.

- [Подключения, хранилища и базы данных](#подключения-хранилища-и-базы-данных)
- [Шифрование](#шифрование)
- [Хранилища — настройки провайдеров](#хранилища--настройки-провайдеров)
- [Подключение к Dashboard](#подключение-к-dashboard)
- [Путь к конфигу](#путь-к-конфигу)
- [Структура файлов в хранилище](#структура-файлов-в-хранилище)

> Настройки сборщика мусора (`GcSettings`) и очистки устаревших бэкапов (`RetentionSettings`) описаны в отдельном файле — [gc-and-retention.md](gc-and-retention.md).
> Настройки автономной очереди бэкапов (`OutboxSettings`) — в разделе [Автономный режим](#автономный-режим) ниже.

---

## Подключения, хранилища и базы данных

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
    "Name": "app-mysql",
    "DatabaseType": "Mysql",
    "Host": "localhost",
    "Port": 3306,
    "Username": "backup",
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
- `SharedBackupPath` / `AgentBackupPath` — **только для MSSQL**, подробнее — в [mssql.md](mssql.md). Для Postgres и MySQL поля не используются.

---

## Шифрование

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

---

## Хранилища — настройки провайдеров

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

---

## Подключение к Dashboard

Token и DashboardUrl передаются через переменные окружения (не в `appsettings.json`):

```bash
AgentSettings__Token=<токен агента из Dashboard>
AgentSettings__DashboardUrl=http://your-server:8080
```

Токен передаётся на сервер через заголовок `X-Agent-Token`. Расписание опрашивается каждые 5 минут.

> **Сетевая прозрачность.** Полный перечень HTTP-запросов агента к дашборду со схемами тел и перечнем полей — в [`NETWORK.md`](../NETWORK.md). Учётные данные БД, ключи шифрования, S3/SFTP-секреты на дашборд **никогда** не уходят.

---

## Путь к конфигу

По умолчанию агент ищет `appsettings.json` в:
- **Docker / Linux:** `/app/config/`
- **Windows:** `{директория exe}\config\`

Переопределяется через переменную окружения `CONFIG_PATH`.

---

## Автономный режим

Если дашборд недоступен, бэкап выполняется штатно, а метаданные записи сохраняются в локальную очередь на диске. Поведение очереди настраивается через `OutboxSettings`:

```json
"OutboxSettings": {
  "ReplayIntervalSeconds": 60
}
```

Блок полностью опционален и в шаблон `appsettings.json` не входит.

- **`ReplayIntervalSeconds`** — как часто воркер пытается дослать накопившиеся записи на дашборд. По умолчанию 60 секунд.

Очередь лежит в `{config}/outbox/`. Записи, которые не удалось дослать после 100 попыток, перемещаются в `outbox/dead/` и агентом больше не обрабатываются.

---

## Структура файлов в хранилище

```
{database}/{yyyy-MM-dd_HH-mm-ss}/
  {database}_{yyyyMMdd_HHmmss}.sql.gz.enc    ← PostgreSQL / MySQL дамп
  {database}_{yyyyMMdd_HHmmss}.bak.enc       ← MSSQL дамп
  manifest.json.gz.enc                       ← манифест файлового бэкапа (если FilePaths непуст)
  manifest.json.enc                          ← легаси-формат старых бэкапов (читается новым агентом)

chunks/{sha256}                              ← общий пул дедуплицированных чанков (S3 only)
```

Манифест нового формата — gzip-сжатый JSON, зашифрованный framed-GCM; reader стримит его через `PipeReader` + `Utf8JsonReader` без полного разворачивания в RAM. Это позволяет бэкапить и восстанавливать файловые хранилища с миллионами мелких файлов без всплеска памяти.

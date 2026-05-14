# Конфигурация

Все настройки в `appsettings.json`. Любой параметр можно переопределить переменной окружения.

- [Подключения, хранилища и базы данных](#подключения-хранилища-и-базы-данных)
- [Наборы файлов (FileSets)](#наборы-файлов-filesets)
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
- `Storages[]` — хранилища для бэкапов (S3, SFTP, Azure Blob, WebDAV или локальный путь); у каждого уникальное имя и собственный набор настроек.
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
- `FilePaths` — список путей к файлам или директориям для файлового бэкапа. Директории обходятся рекурсивно. Файлы режутся на content-defined chunks (FastCDC, ~4 МиБ) и дедуплицируются внутри одного хранилища. Работает на S3, Azure Blob и LocalFs (везде, где есть дешёвый `HEAD`/листинг для дедупа). На SFTP и WebDAV дамп загрузится, файлы пропустятся с warning. Поле необязательное.
- `SharedBackupPath` / `AgentBackupPath` — **только для MSSQL**, подробнее — в [mssql.md](mssql.md). Для Postgres и MySQL поля не используются.
- `BinPath` — **для PostgreSQL и MySQL**, необязательное. Каталог с клиентскими бинарниками: `pg_dump`/`pg_basebackup`/`psql`/`pg_ctl` для PG, `mysqldump`/`mysql` для MySQL. Override авто-резолва. По умолчанию агент сам ищет клиент: для PG — под мажорную версию сервера (реестр Windows + стандартные каталоги установки → `PATH`); для MySQL — `C:\Program Files\MySQL\MySQL Server *\bin` (высшая версия) / `/usr/local/mysql/bin` → `PATH`. Задавайте поле только при нестандартной установке, когда авто-резолв не находит нужный каталог, либо когда `PATH` Windows-службы не содержит MySQL/PG bin (типичный случай — инсталлятор положил каталог только в `User PATH`). Для MSSQL поле не используется. Подробнее — [postgres.md](postgres.md), [mysql.md](mysql.md).

---

## Наборы файлов (FileSets)

`FileSets[]` — отдельный список для бэкапа произвольных каталогов и файлов без привязки к базе данных. Подходит для загруженных пользователями файлов приложения, конфигов, сертификатов и т. п.

```json
"FileSets": [
  {
    "Name": "app-uploads",
    "StorageName": "prod-s3",
    "Paths": [
      "/var/www/uploads",
      "/etc/myapp/certs"
    ]
  }
]
```

- `Name` — уникальное имя набора. Используется как идентификатор в дашборде.
- `StorageName` — ссылается на запись в `Storages[]` по имени. Должно указывать на S3-, Azure Blob- или LocalFs-хранилище — SFTP и WebDAV для file-set'ов не поддерживаются.
- `Paths` — список путей к файлам или директориям. Директории обходятся рекурсивно.

Пайплайн: **Open → Capture → Finalize** — тот же content-defined chunking (FastCDC, ~4 МиБ) и дедупликация по sha256, что и у файлового этапа БД-бэкапа. Дамп базы не создаётся.

Расписание — индивидуальное per-file-set, задаётся через дашборд.

**Работает на S3, Azure Blob и LocalFs.** При указании SFTP- или WebDAV-хранилища бэкап завершится с ошибкой и статусом `failed` в дашборде.

> Набор файлов регистрируется в дашборде автоматически при первом открытии записи бэкапа — отдельной настройки через UI не требуется.

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

Каждая запись в `Storages[]` имеет поле `Provider` (`S3`, `Sftp`, `AzureBlob`, `WebDav` или `LocalFs`) и соответствующий вложенный блок с тем же именем. Лишние блоки для выбранного провайдера игнорируются.

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

> **Файловый бэкап (`FilePaths`) не работает на SFTP- и WebDAV-хранилищах** — у этих протоколов нет дешёвого `HEAD` и префикс-листинга для дедупликации кусков. При непустом `FilePaths` для базы, смотрящей на SFTP/WebDAV, дамп загрузится, файлы будут пропущены с warning. На LocalFs file-backup и file-set'ы поддерживаются полностью (`File.Exists` + рекурсивный `Directory.EnumerateFiles`).

**Azure Blob-хранилище:**

```json
{
  "Name": "prod-azure",
  "Provider": "AzureBlob",
  "AzureBlob": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net",
    "ContainerName": "prod-backups"
  }
}
```

Альтернативно — без `ConnectionString`, тройкой `AccountName + AccountKey + ServiceUri`:

```json
{
  "Name": "prod-azure",
  "Provider": "AzureBlob",
  "AzureBlob": {
    "AccountName": "mystorage",
    "AccountKey": "<base64 от Azure>",
    "ServiceUri": "https://mystorage.blob.core.windows.net",
    "ContainerName": "prod-backups"
  }
}
```

- `ContainerName` обязателен. Контейнер создавать заранее не нужно — агент создаст автоматически при первой загрузке.
- Задайте либо `ConnectionString`, либо все три поля `AccountName` + `AccountKey` + `ServiceUri`. Если переданы оба варианта — приоритет у `ConnectionString`.
- `ServiceUri` обычно вида `https://<account>.blob.core.windows.net`. Для Azure Government / Stack Hub — соответствующий региональный домен.
- Поддерживается весь спектр: дамп, файловый бэкап с дедупликацией, file-set'ы, chunk GC, retention sweep.

**WebDAV-хранилище:**

```json
{
  "Name": "yandex-disk",
  "Provider": "WebDav",
  "WebDav": {
    "BaseUrl": "https://webdav.yandex.ru",
    "Username": "you@yandex.ru",
    "Password": "<пароль приложения>",
    "RemotePath": "/backups"
  }
}
```

- `BaseUrl` обязателен и должен начинаться с `http://` или `https://`. Голый `http://` — credentials и тело бэкапа уйдут в открытом виде, агент один раз пишет warning; для prod используйте `https`.
- `Username` / `Password` — basic-auth. Для Яндекс.Диска **обязательно** [пароль приложения](https://id.yandex.ru/security/app-passwords), не основной пароль аккаунта (двухфакторка ломает обычную авторизацию по WebDAV).
- `RemotePath` — базовый каталог под аккаунтом. Дефолт `/`. Промежуточные каталоги (`MKCOL`) создаются автоматически при первой загрузке.
- Покрывает Яндекс.Диск, Облако МТС и любые WebDAV-совместимые серверы (Nextcloud / ownCloud / Apache mod_dav).
- Файловый бэкап и chunk GC — **не поддерживаются** (см. предупреждение выше). Дамп БД, restore и retention работают штатно.

**Локальная папка (LocalFs):**

```json
{
  "Name": "local-disk",
  "Provider": "LocalFs",
  "LocalFs": {
    "RemotePath": "D:/backups"
  }
}
```

- `RemotePath` обязателен. Это папка на хосте агента, в которую агент кладёт зашифрованные дампы. Может быть как локальным диском (`D:/backups`, `/var/backups`), так и заранее смонтированной сетевой шарой (NFS, CIFS/SMB, iSCSI-том) — для агента это просто путь.
- Промежуточные подкаталоги создаются автоматически. Запись атомарна — файл сначала пишется во временный `.upload-tmp` рядом с целевым именем и только после успешного копирования переименовывается на финальное имя; при удалении пустые подкаталоги вычищаются до `RemotePath`.
- Защита от выхода за корень: agent резолвит итоговый путь через `Path.GetFullPath` и проверяет, что он лежит под `RemotePath`. Битый или вредоносный `objectKey` (например, с `..`) приводит к явной ошибке, не к записи в произвольное место ФС.
- Никаких credentials в этом блоке нет — на дашборд тоже ничего не уходит. Доступ к каталогу обеспечивается правами ФС: на Linux — владелец/группа процесса агента, на Windows — ACL на каталог; на сетевых шарах — права на mount.
- Файловый бэкап (`FilePaths`/`FileSets`), дедупликация чанков в общий пул `chunks/{sha256}` и chunk GC — **поддерживаются полностью**, как на S3 и Azure Blob. На сетевой шаре с миллионами чанков рекурсивный листинг каталога `chunks/` будет упираться в скорость SMB/NFS — для локального диска вопросов нет.

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
  "ReplayIntervalSeconds": 60,
  "MaxEntries": 1000,
  "MaxAgeDays": 14
}
```

Блок полностью опционален и в шаблон `appsettings.json` не входит.

- **`ReplayIntervalSeconds`** — как часто воркер пытается дослать накопившиеся записи на дашборд. По умолчанию 60 секунд (минимум 10).
- **`MaxEntries`** — верхний предел длины очереди. Если записей больше — самые старые (по `QueuedAt`) уезжают в `outbox/dead/` с reason `exceeded max entries (N)`. По умолчанию 1000. Значение `0` или меньше отключает лимит.
- **`MaxAgeDays`** — записи старше N дней (по `QueuedAt`) уезжают в `outbox/dead/` с reason `exceeded max age (N days)`. По умолчанию 14. Значение `0` или меньше отключает лимит.

Обрезка по возрасту и по числу выполняется в начале каждого тика replay-воркера (до попытки дослать), порядок: сначала по возрасту, потом — если очередь всё ещё над лимитом — по количеству.

Очередь лежит в `{config}/outbox/`. Туда же попадают записи, которые не удалось дослать после 100 попыток (`exceeded 100 replay attempts`), и те, что отвергнуты дашбордом как permanent (см. `DashboardAvailabilityPolicy`). Содержимое `outbox/dead/` агентом больше не обрабатывается — оператор может разобрать руками.

---

## Структура файлов в хранилище

```
{database}/{yyyy-MM-dd_HH-mm-ss}/
  {database}_{yyyyMMdd_HHmmss}.sql.gz.enc    ← PostgreSQL / MySQL дамп (logical)
  {database}_{yyyyMMdd_HHmmss}.tar.gz.enc    ← PostgreSQL physical (pg_basebackup, архив PGDATA + WAL)
  {database}_{yyyyMMdd_HHmmss}.bacpac.enc    ← MSSQL дамп (logical, через DacFx)
  {database}_{yyyyMMdd_HHmmss}.bak.enc       ← MSSQL дамп (physical, через BACKUP DATABASE)
  manifest.json.gz.enc                       ← манифест файлового бэкапа (если FilePaths непуст)
  manifest.json.enc                          ← легаси-формат старых бэкапов (читается новым агентом)

chunks/{sha256}                              ← общий пул дедуплицированных чанков (S3 / Azure Blob / LocalFs)
```

Манифест нового формата — gzip-сжатый JSON, зашифрованный framed-GCM; reader стримит его через `PipeReader` + `Utf8JsonReader` без полного разворачивания в RAM. Это позволяет бэкапить и восстанавливать файловые хранилища с миллионами мелких файлов без всплеска памяти.

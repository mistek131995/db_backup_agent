# Backupster Agent — сетевая прозрачность

Этот документ — полный перечень HTTP-запросов, которые агент (`BackupsterAgent`) делает к дашборду. Для каждого запроса указаны метод, URL, заголовки, точная схема тела и пример.

Цель документа — дать администратору возможность убедиться: **с хоста, где стоит агент, на дашборд уходит только сетевая топология (имена, хосты, порты, имена БД), но никогда — учётные данные подключений к БД, ключи шифрования, S3/SFTP-секреты**.

Документ обязан обновляться вместе с кодом в том же PR, что и изменения сетевого поведения агента.

---

## Общие принципы

### Аутентификация

Каждый запрос к дашборду содержит заголовок:

```
X-Agent-Token: <AgentSettings.Token>
```

Токен задаётся через env var `AgentSettings__Token` (см. README агента). В теле запроса и в query-строке токен не передаётся никогда. В логах агента печатается только префикс `token[..8]`.

### Формат enum по сети

Все enum сериализуются в **camelCase** (`JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`). Примеры: `inProgress`, `encryptingDump`, `downloadingDump`, `success`, `failed`, `partial`.

В БД дашборда те же enum хранятся как `.ToString()` (`"InProgress"`, `"EncryptingDump"` и т. д.) — это внутренний формат, не сетевой.

### Транспорт

Все запросы — обычный HTTP/HTTPS на `AgentSettings.DashboardUrl`. Content-Type тела — `application/json; charset=utf-8`. Ответы без тела — `204 No Content`.

### Таймауты HTTP-клиентов

Верхние лимиты на один HTTP-вызов (задаются в `Program.cs` через `AddHttpClient(c => c.Timeout = ...)`), чтобы агент не зависал на проблемах сети:

- `BackupRecordClient`, `ScheduleService`, `ConnectionSyncService`, `DatabaseSyncService`, `FileSetSyncService`, `RetentionClient` — **20 с** на вызов.
- `AgentTaskClient` — **60 с** на вызов (покрывает long-poll 30 с с запасом).
- Progress-вызовы (`BackupRecordClient.ReportProgressAsync`, `AgentTaskClient.ReportProgressAsync`) поверх этого дополнительно ограничены `CancellationTokenSource.CancelAfter(3 с)` — heartbeat не должен ни при каких условиях задерживать пайплайн.

Polly-ретраи (1/2/4 с) срабатывают поверх этих лимитов. Общее время на одну операцию с ретраями — `таймаут × 3 + 7 с` в худшем случае.

### Что никогда не уходит на дашборд

- `Connections[].Username`, `Connections[].Password`
- `EncryptionSettings.Key`
- `Storages[].S3.AccessKey`, `Storages[].S3.SecretKey`
- `Storages[].Sftp.Password`, `Storages[].Sftp.PrivateKeyPath`, `Storages[].Sftp.PrivateKeyPassphrase`
- `Storages[].AzureBlob.ConnectionString`, `Storages[].AzureBlob.AccountKey`
- Содержимое дампов, чанков, файлов (весь payload бэкапа шифруется AES-256-GCM и идёт напрямую в ваш S3/SFTP, минуя дашборд)

Если вы нашли в выхлопе агента или в трафике что-то из этого списка — это баг. Пишите в репозиторий.

---

## 1. Backup — открыть запись

Первый шаг бэкап-пайплайна. Агент просит дашборд завести запись и вернуть `id`, чтобы дальше слать прогресс и финализацию по этому id.

- **Метод / URL:** `POST {DashboardUrl}/api/v1/agent/backup-record`
- **Клиент:** `Services/Dashboard/Clients/BackupRecordClient.OpenAsync`
- **Retry:** Polly, 3 попытки (1/2/4 с)
- **Ожидаемые ответы:** `201 Created` с телом, `404 Not Found` (БД не зарегистрирована у агента), `401 Unauthorized`

### Тело запроса (`OpenBackupRecordDto`)

Пример для БД-бэкапа:

```json
{
  "databaseName": "mydb",
  "connectionName": "main-pg",
  "storageName": "prod-s3",
  "startedAt": "2026-04-19T03:00:00.000Z"
}
```

Пример для file-set бэкапа (`ConnectionName` пустой, `DatabaseName` дублирует имя file-set):

```json
{
  "databaseName": "config-backups",
  "connectionName": "",
  "storageName": "prod-s3",
  "startedAt": "2026-04-19T03:00:00.000Z",
  "databaseType": "fileSet",
  "fileSetName": "config-backups"
}
```

| Поле             | Тип             | Описание                                                              |
|------------------|-----------------|-----------------------------------------------------------------------|
| `databaseName`   | string          | Имя БД из `Databases[].Database`. Для file-set — дублирует `fileSetName` |
| `connectionName` | string          | Имя подключения из `Databases[].ConnectionName` (должно быть в `Connections[]`). Для file-set — пустая строка |
| `storageName`    | string          | Имя хранилища из `Databases[].StorageName` (должно быть в `Storages[]`). Сервер сохраняет его в записи, чтобы при retention-чистке знать, какой агент-storage обслуживает этот бэкап |
| `startedAt`      | datetime (UTC)? | Опционально. Момент реального старта бэкапа по часам агента (ISO 8601). Если не передан — сервер использует `DateTime.UtcNow`. Агент отправляет это поле всегда; при replay накопленных offline-записей передаётся реальное время старта бэкапа, случившегося до того, как дашборд стал доступен, чтобы `StartedAt` в UI не сдвигался на момент replay'я |
| `databaseType`   | enum (string)?  | `postgres`, `mysql`, `mssql`, `fileSet`. Для cron-бэкапов БД не передаётся (`null`) — сервер берёт тип из ранее зарегистрированной `AgentDatabase`. Для file-set — `fileSet`, именно по этому полю сервер отличает file-set run и авто-регистрирует `AgentDatabase` с типом `FileSet` при первом открытии |
| `fileSetName`    | string?         | Имя file-set из `FileSets[].Name`. Заполняется только для file-set run, иначе `null` |
| `backupMode`     | enum (string)?  | `logical` или `physical`. Режим, в котором агент снимает этот бэкап. `null` для file-set (не применимо) и для старых агентов, которые не шлют поле — в этом случае сервер инференсит по `AgentDatabase.DatabaseType`: `Mssql` → `Physical`, остальные → `Logical` (backward-compat) |

### Тело ответа (`OpenBackupRecordResponseDto`)

```json
{
  "id": "0f4c1c1e-7b8f-4e9a-9f2b-1b3a4c5d6e7f"
}
```

---

## 2. Backup — прогресс

Heartbeat-отчёт о текущей стадии бэкапа. Шлётся не чаще раза в 5 секунд + немедленно при смене стадии. Ошибки swallow-ятся (не ломают бэкап).

- **Метод / URL:** `POST {DashboardUrl}/api/v1/agent/backup-record/{id}/progress`
- **Клиент:** `Services/Dashboard/Clients/BackupRecordClient.ReportProgressAsync`
- **Retry:** нет. Таймаут 3 с
- **Ожидаемые ответы:** `204 No Content`, `403 Forbidden` (чужая запись), `404 Not Found`, `409 Conflict` (запись уже финализирована)

### Тело запроса (`BackupProgressDto`)

```json
{
  "stage": "uploadingDump",
  "processed": 52428800,
  "total": 104857600,
  "unit": "bytes",
  "currentItem": null
}
```

| Поле          | Тип           | Описание                                                                              |
|---------------|---------------|---------------------------------------------------------------------------------------|
| `stage`       | enum (string) | Одно из: `dumping`, `encryptingDump`, `uploadingDump`, `capturingFiles`               |
| `processed`   | long?         | Обработано единиц (байты для дампа, файлы для `capturingFiles`)                       |
| `total`       | long?         | Всего единиц (может быть `null`, если неизвестно заранее)                             |
| `unit`        | string?       | `"bytes"` или `"files"`                                                               |
| `currentItem` | string?       | Имя текущего файла для `capturingFiles` (не используется для стадий дампа)            |

---

## 3. Backup — финализация

Закрывает запись финальным статусом. Идемпотентно: повторная финализация той же записи возвращает `204` без изменений.

- **Метод / URL:** `PATCH {DashboardUrl}/api/v1/agent/backup-record/{id}`
- **Клиент:** `Services/Dashboard/Clients/BackupRecordClient.FinalizeAsync`
- **Retry:** Polly, 3 попытки (1/2/4 с)
- **Ожидаемые ответы:** `204 No Content`, `400 Bad Request` (статус `inProgress` в финализации), `401 Unauthorized`

### Тело запроса (`FinalizeBackupRecordDto`)

```json
{
  "status": "success",
  "sizeBytes": 104857600,
  "durationMs": 42318,
  "dumpObjectKey": "mydb/2026-04-19_03-00-00/dump.sql.gz.enc",
  "errorMessage": null,
  "backupAt": "2026-04-19T03:00:42.318Z",
  "manifestKey": "mydb/2026-04-19_03-00-00/manifest.json.enc",
  "filesCount": 142,
  "filesTotalBytes": 78430210,
  "newChunksCount": 39,
  "fileBackupError": null
}
```

| Поле              | Тип            | Описание                                                                                                  |
|-------------------|----------------|-----------------------------------------------------------------------------------------------------------|
| `status`          | enum (string)  | `success` или `failed`                                                                                    |
| `sizeBytes`       | long?          | Размер зашифрованного дампа                                                                               |
| `durationMs`      | long?          | Длительность всего пайплайна                                                                              |
| `dumpObjectKey`   | string?        | Ключ объекта в хранилище — на случай `Failed` может быть `null`, если дамп не успел загрузиться           |
| `errorMessage`    | string?        | Человекочитаемый текст ошибки. Не `ex.Message` с внутренностями                                           |
| `backupAt`        | datetime (UTC) | Момент завершения (ISO 8601)                                                                              |
| `manifestKey`     | string?        | Ключ манифеста файлового бэкапа. `null`, если файлы не бэкапили                                           |
| `filesCount`      | int?           | Количество файлов в манифесте                                                                             |
| `filesTotalBytes` | long?          | Суммарный размер файлов                                                                                   |
| `newChunksCount`  | int?           | Сколько чанков реально загрузилось (остальные уже были в хранилище благодаря дедупу)                      |
| `fileBackupError` | string?        | Текст ошибки этапа файлов. `null`, если этап не запускался или прошёл успешно                             |

---

## 4. Schedule — получить расписание (+ heartbeat)

Агент забирает актуальное расписание каждые 5 минут. Тот же запрос работает как heartbeat — дашборд обновляет `Agent.LastSeenAt`.

- **Метод / URL:** `GET {DashboardUrl}/api/v1/agent/schedule`
- **Клиент:** `Services/Dashboard/Clients/ScheduleService`
- **Retry:** Polly, 3 попытки (1/2/4 с)
- **Тело запроса:** пустое
- **Ожидаемые ответы:** `200 OK` c телом, `401 Unauthorized`

### Тело ответа (`ScheduleDto`)

```json
{
  "cronExpression": "0 3 * * *",
  "isActive": true,
  "overrides": [
    {
      "databaseName": "reporting",
      "cronExpression": "0 */2 * * *",
      "isActive": true
    }
  ]
}
```

| Поле             | Тип                        | Описание                                                      |
|------------------|----------------------------|---------------------------------------------------------------|
| `cronExpression` | string                     | Дефолтный cron агента                                         |
| `isActive`       | bool                       | `false` = расписание временно отключено                       |
| `overrides`      | `ScheduleOverrideDto[]?`   | Per-database override'ы (по сети — JSON-массив; в коде агента — `List<ScheduleOverrideDto>?`). Применяется, если `isActive=true` |

---

## 5. Connection sync

Отправляется один раз на старте (и после успеха — останавливается). Повторные попытки внешние: backoff 10 с → 5 мин до первого успеха.

- **Метод / URL:** `POST {DashboardUrl}/api/v1/agent/connections`
- **Клиент:** `Services/Dashboard/Sync/ConnectionSyncService`
- **Retry:** Polly, 3 попытки (1/2/4 с)
- **Ожидаемые ответы:** `204 No Content`, `400 Bad Request`, `401 Unauthorized`

### Тело запроса (`ConnectionSyncRequestDto`)

```json
{
  "connections": [
    {
      "name": "main-pg",
      "databaseType": "Postgres",
      "host": "db-prod.internal",
      "port": 5432
    },
    {
      "name": "reporting-mssql",
      "databaseType": "Mssql",
      "host": "10.0.2.15",
      "port": 1433
    }
  ]
}
```

| Поле (элемента)  | Тип    | Описание                                                                 |
|------------------|--------|--------------------------------------------------------------------------|
| `name`           | string | Имя подключения из `Connections[].Name`                                  |
| `databaseType`   | string | `Postgres`, `Mysql`, `Mssql` (строкой, PascalCase — это не enum, а сырое поле `DatabaseType` как в конфиге) |
| `host`           | string | Хост из `Connections[].Host`                                             |
| `port`           | int    | Порт из `Connections[].Port`                                             |

> **Ни `Username`, ни `Password` не попадают в это тело.** Формируется в `ConnectionSyncService.BuildPayload()` — при изменениях проверяйте, что оно по-прежнему берёт только эти четыре поля.

---

## 6. Database sync

Второй топологический sync: агент сообщает дашборду свой список БД (без file-set'ов). Отправляется один раз на старте, после успеха — останавливается. Внешний backoff 10 с → 5 мин до первого успеха.

- **Метод / URL:** `POST {DashboardUrl}/api/v1/agent/databases`
- **Клиент:** `Services/Dashboard/Sync/DatabaseSyncService`
- **Retry:** Polly, 3 попытки (1/2/4 с)
- **Ожидаемые ответы:** `204 No Content`, `400 Bad Request`, `401 Unauthorized`

### Тело запроса (`DatabaseSyncRequestDto`)

```json
{
  "databases": [
    {
      "name": "mydb",
      "databaseType": "Postgres"
    },
    {
      "name": "reporting",
      "databaseType": "Mssql"
    }
  ]
}
```

| Поле (элемента) | Тип    | Описание                                                                                                   |
|-----------------|--------|------------------------------------------------------------------------------------------------------------|
| `name`          | string | Имя БД из `Databases[].Database`                                                                           |
| `databaseType`  | string | `Postgres`, `Mysql`, `Mssql` (PascalCase — это сырой `ToString()` от enum, не camelCase-enum по сети). Резолвится из `Connections[].DatabaseType` для этой БД через `ConnectionResolver` |

Бэкенд делает upsert по `(AgentId, Name)` среди записей, у которых `DatabaseType != FileSet`. Stale-записи (удалённые из конфига) **не чистятся** — история бэкапов сохраняется. Записи с пустым `Database` или неизвестным `ConnectionName` в payload не попадают (warning в лог).

---

## 7. FileSet sync

Третий топологический sync: агент сообщает дашборду свой список file-set'ов. Отправляется один раз на старте, после успеха — останавливается. Внешний backoff 10 с → 5 мин до первого успеха.

- **Метод / URL:** `POST {DashboardUrl}/api/v1/agent/filesets`
- **Клиент:** `Services/Dashboard/Sync/FileSetSyncService`
- **Retry:** Polly, 3 попытки (1/2/4 с)
- **Ожидаемые ответы:** `204 No Content`, `400 Bad Request`, `401 Unauthorized`

### Тело запроса (`FileSetSyncRequestDto`)

```json
{
  "fileSets": [
    {
      "name": "config-backups",
      "storageName": "prod-s3"
    }
  ]
}
```

| Поле (элемента) | Тип    | Описание                                                                 |
|-----------------|--------|--------------------------------------------------------------------------|
| `name`          | string | Имя file-set из `FileSets[].Name`                                        |
| `storageName`   | string | Имя хранилища из `FileSets[].StorageName` (должно быть в `Storages[]`)   |

Бэкенд делает upsert по `(AgentId, Name)` среди записей с `DatabaseType = FileSet`. Stale-записи не чистятся. Коллизия: имя file-set не может совпасть с именем зарегистрированной БД на том же агенте — такой запрос отклоняется с `BadRequest`. Записи с пустым `Name` или неизвестным `StorageName` в payload не попадают (warning в лог).

---

## 8. Task-канал — long-poll задачи

Агент опрашивает дашборд каждые 30 секунд (long-poll). Первый запрос сразу после старта; после выполнения задачи — сразу следующий; при 5xx/сетевых ошибках backoff 10 с → 5 мин.

Канал единый для всех типов юзерских задач: `restore`, `delete`, `backup`. Все три ветки на стороне агента активны.

- **Метод / URL:** `GET {DashboardUrl}/api/v1/agent/task`
- **Клиент:** `Services/Dashboard/Clients/AgentTaskClient.FetchTaskAsync`
- **Тело запроса:** пустое
- **Ожидаемые ответы:** `204 No Content` (задач нет), `200 OK` с телом, `401 Unauthorized`

### Тело ответа (`AgentTaskForAgentDto`)

```json
{
  "id": "7a2e1d8f-9b4c-4a1f-b5c6-1d2e3f4a5b6c",
  "type": "restore",
  "restore": {
    "sourceDatabaseName": "mydb",
    "dumpObjectKey": "mydb/2026-04-18_03-00-00/dump.sql.gz.enc",
    "targetDatabaseName": "mydb_restore",
    "manifestKey": "mydb/2026-04-18_03-00-00/manifest.json.enc",
    "targetFileRoot": "/var/data/myapp",
    "targetConnectionName": "main-pg",
    "storageName": "prod-s3"
  }
}
```

| Поле      | Тип           | Описание                                                                                         |
|-----------|---------------|--------------------------------------------------------------------------------------------------|
| `id`      | Guid          | ID задачи; используется во всех последующих запросах                                             |
| `type`    | enum (string) | `restore`, `delete`, `backup`                                                                    |
| `restore` | object?       | Payload для `type=restore`. Для других типов — `null`                                            |
| `delete`  | object?       | Payload для `type=delete`. Для других типов — `null`                                             |
| `backup`  | object?       | Payload для `type=backup`. Для других типов — `null`                                             |

#### `restore` payload (`RestoreTaskPayload`)

| Поле                   | Тип     | Описание                                                                                           |
|------------------------|---------|----------------------------------------------------------------------------------------------------|
| `sourceDatabaseName`   | string  | Имя исходной БД (для резолва `DatabaseConfig`, если `storageName` не пришёл)                       |
| `dumpObjectKey`        | string  | Ключ дампа в хранилище                                                                             |
| `targetDatabaseName`   | string? | Куда восстановить БД. `null` = тот же `sourceDatabaseName`                                         |
| `manifestKey`          | string? | Ключ манифеста файлов. `null` = файловой части нет, восстанавливаем только БД                      |
| `targetFileRoot`       | string? | Куда класть файлы. `null` = в служебную папку агента (`RestoreSettings.FileRestoreBasePath`, дефолт `restore-files/`); она очищается перед каждым restore и доступна только на хосте агента |
| `targetConnectionName` | string? | Override подключения. `null` = подключение из `DatabaseConfig` исходной БД                         |
| `storageName`          | string? | Override хранилища. `null` = хранилище из `DatabaseConfig` исходной БД                             |
| `backupMode`           | enum (string)? | `logical` или `physical` — режим, в котором был снят бэкап. Если `null` (старый дашборд), агент инференсит по `DatabaseType`: `Mssql` → `Physical`, остальные → `Logical` |

Пример `type=delete`:

```json
{
  "id": "1b7c8d2e-3f4a-5b6c-7d8e-9f0a1b2c3d4e",
  "type": "delete",
  "delete": {
    "storageName": "prod-s3",
    "dumpObjectKey": "mydb/2026-04-10_03-00-00/dump.sql.gz.enc",
    "manifestKey": "mydb/2026-04-10_03-00-00/manifest.json.enc"
  }
}
```

#### `delete` payload (`DeleteTaskPayload`)

| Поле            | Тип     | Описание                                                                                                   |
|-----------------|---------|------------------------------------------------------------------------------------------------------------|
| `storageName`   | string  | Имя хранилища из `Storages[]`, в котором лежат удаляемые объекты                                           |
| `dumpObjectKey` | string? | Ключ дампа. `null`, если бэкап упал до загрузки — удалять нечего                                           |
| `manifestKey`   | string? | Ключ манифеста файлов. `null` = файловой части не было                                                     |

Агент удаляет оба ключа (best-effort, 404 swallow); чанки, на которые манифест ссылался, становятся непривязанными — `ChunkGcWorker` уберёт их через `GcSettings.GraceHours` (дефолт 24).

Пример `type=backup` для БД (ручной «Backup Now» по БД):

```json
{
  "id": "2c8d9e3f-4a5b-6c7d-8e9f-0a1b2c3d4e5f",
  "type": "backup",
  "backup": {
    "databaseName": "mydb",
    "fileSetName": null
  }
}
```

Пример `type=backup` для file-set (ручной «Backup Now» по file-set):

```json
{
  "id": "3d9e0f4a-5b6c-7d8e-9f0a-1b2c3d4e5f60",
  "type": "backup",
  "backup": {
    "databaseName": "",
    "fileSetName": "config-backups"
  }
}
```

#### `backup` payload (`BackupTaskPayload`)

| Поле           | Тип             | Описание                                                                                                                                          |
|----------------|-----------------|---------------------------------------------------------------------------------------------------------------------------------------------------|
| `databaseName` | string          | Имя БД из `Databases[].Database` — агент находит `DatabaseConfig` и запускает `BackupJob`. Для file-set — пустая строка                           |
| `fileSetName`  | string?         | Имя file-set из `FileSets[].Name` — агент находит `FileSetConfig` и запускает `FileSetBackupJob`. `null` для БД-бэкапа                            |
| `backupMode`   | enum (string)?  | `logical` или `physical`. Режим, в котором агент должен снять бэкап. `null` (или поле отсутствует) → агент использует `logical`. Для file-set не применимо |

Прогресс бэкапа идёт через **record-канал** (`/api/v1/agent/backup-record/{id}/progress`, секция 3), не через task-progress — это те же стадии, что у cron-бэкапов. Task-строка в «Историю задач» показывает только финальный статус; интерактивный прогресс UI берёт из соответствующего `BackupRecord`.

---

## 9. Task — прогресс

Heartbeat задачи. Шлётся не чаще раза в 5 секунд + немедленно при смене стадии. Ошибки swallow-ятся.

- **Метод / URL:** `POST {DashboardUrl}/api/v1/agent/task/{id}/progress`
- **Клиент:** `Services/Dashboard/Clients/AgentTaskClient.ReportProgressAsync`
- **Retry:** нет. Таймаут 3 с
- **Ожидаемые ответы:** `204 No Content`, `403 Forbidden`, `409 Conflict` (задача уже не `InProgress`)

### Тело запроса (`AgentTaskProgressDto`)

```json
{
  "stage": "restoringFiles",
  "processed": 84,
  "total": 142,
  "unit": "files",
  "currentItem": "/var/data/myapp/assets/logo.png"
}
```

| Поле          | Тип     | Описание                                                                                                                                                                  |
|---------------|---------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `stage`       | string  | camelCase-имя стадии. Словарь значений зависит от `type` задачи: для `restore` — `downloadingDump`, `decryptingDump`, `decompressingDump`, `preparingDatabase`, `restoringDatabase`, `downloadingManifest`, `restoringFiles`; для `delete` — `resolving`, `deletingManifest`, `deletingDump`, `completed`. Для `type=backup` task-progress не отправляется — прогресс идёт через record-канал (секция 3) |
| `processed`   | long?   | Обработано единиц                                                                                                                                                         |
| `total`       | long?   | Всего единиц                                                                                                                                                              |
| `unit`        | string? | `"bytes"` или `"files"`                                                                                                                                                   |
| `currentItem` | string? | Имя текущего файла/объекта (иначе `null`)                                                                                                                                 |

---

## 10. Task — финализация

Закрывает задачу финальным статусом. Идемпотентно.

- **Метод / URL:** `PATCH {DashboardUrl}/api/v1/agent/task/{id}`
- **Клиент:** `Services/Dashboard/Clients/AgentTaskClient.PatchTaskAsync`
- **Retry:** Polly, 3 попытки (1/2/4 с)
- **Ожидаемые ответы:** `204 No Content`, `400 Bad Request`, `403 Forbidden`

### Тело запроса (`PatchAgentTaskDto`)

```json
{
  "status": "partial",
  "errorMessage": "2 файла не удалось восстановить: permission denied ...",
  "restore": {
    "databaseStatus": "success",
    "filesStatus": "partial",
    "filesRestoredCount": 140,
    "filesFailedCount": 2
  }
}
```

| Поле           | Тип           | Описание                                                                                                      |
|----------------|---------------|---------------------------------------------------------------------------------------------------------------|
| `status`       | enum (string) | `success`, `failed`, `partial`                                                                                |
| `errorMessage` | string?       | Человекочитаемое сообщение. Для `partial` — список первых 20 ошибок, обрезанный до 2000 символов              |
| `restore`      | object?       | Type-specific результат для `type=restore`. Для других типов — `null`                                         |
| `backup`       | object?       | Type-specific результат для `type=backup`. Для других типов — `null`                                          |

#### `restore` result (`RestoreTaskResult`)

| Поле                 | Тип           | Описание                                                                                        |
|----------------------|---------------|-------------------------------------------------------------------------------------------------|
| `databaseStatus`     | enum (string) | `success`, `failed`. `null` — задача не затрагивала БД                                          |
| `filesStatus`        | enum (string) | `success`, `failed`, `partial`, `skipped`. `null` — задача не затрагивала файлы                 |
| `filesRestoredCount` | int?          | Сколько файлов успешно восстановилось                                                           |
| `filesFailedCount`   | int?          | Сколько файлов не удалось восстановить                                                          |

#### `backup` result (`BackupTaskResult`)

| Поле             | Тип   | Описание                                                                                          |
|------------------|-------|---------------------------------------------------------------------------------------------------|
| `backupRecordId` | Guid? | ID записи `BackupRecord`, созданной в record-канале. Дашборд связывает таск с записью по этому ID |

Для `type=delete` отдельного `result`-поля нет: исход сообщается только через `status` + `errorMessage`.

---

## 11. Retention — забрать просроченные записи

Агент-оркестратор retention периодически (раз в `RetentionSettings.IntervalHours`, дефолт 6 часов) забирает у дашборда пачку записей, у которых истёк срок хранения по тарифу пользователя.

- **Метод / URL:** `GET {DashboardUrl}/api/v1/agent/backup-records/expired?limit=N`
- **Клиент:** `Services/Dashboard/Clients/RetentionClient.GetExpiredAsync`
- **Retry:** нет
- **Тело запроса:** пустое
- **Ожидаемые ответы:** `200 OK` с массивом, `401 Unauthorized`

Сервер фильтрует записи: только этого агента, статус не `inProgress`, `BackupAt < NOW − retentionDays(plan)`, `StorageName` непустой, `StorageUnreachableAt IS NULL` (записи, ранее помеченные как «нет хранилища», в выдачу не попадают — иначе агент бы зацикливался).

### Тело ответа (`ExpiredBackupRecordDto[]`)

```json
[
  {
    "id": "0f4c1c1e-7b8f-4e9a-9f2b-1b3a4c5d6e7f",
    "storageName": "prod-s3",
    "dumpObjectKey": "mydb/2026-04-01_03-00-00/dump.sql.gz.enc",
    "manifestKey": "mydb/2026-04-01_03-00-00/manifest.json.enc"
  }
]
```

| Поле            | Тип     | Описание                                                                                  |
|-----------------|---------|-------------------------------------------------------------------------------------------|
| `id`            | Guid    | ID записи                                                                                 |
| `storageName`   | string  | Имя хранилища, в котором лежат `dumpObjectKey` и `manifestKey`                            |
| `dumpObjectKey` | string? | Ключ дампа в хранилище. `null`, если бэкап упал до загрузки                               |
| `manifestKey`   | string? | Ключ манифеста файлов. `null`, если файловой части не было                                |

---

## 12. Retention — удалить запись

После того как агент удалил `dumpObjectKey` и `manifestKey` из хранилища, он закрывает запись.

- **Метод / URL:** `DELETE {DashboardUrl}/api/v1/agent/backup-records/{id}`
- **Клиент:** `Services/Dashboard/Clients/RetentionClient.DeleteAsync`
- **Retry:** Polly, 3 попытки (1/2/4 с)
- **Тело запроса:** пустое
- **Ожидаемые ответы:** `204 No Content`, `403 Forbidden` (запись чужого агента), `401 Unauthorized`

Идемпотентно: повторный DELETE на уже удалённую запись возвращает `204`.

---

## 13. Retention — пометить запись как «нет хранилища»

Если для записи в выдаче `/expired` агент не нашёл `storageName` в своём конфиге `Storages[]`, он одним батчем сообщает об этом дашборду. Бэкенд выставляет таким записям `StorageUnreachableAt = NOW`, после чего они исключаются из последующих выдач `/expired` (см. фильтр в #9). UI покажет такую запись с пометкой «хранилище не найдено» и предложит ручное действие.

- **Метод / URL:** `POST {DashboardUrl}/api/v1/agent/backup-records/mark-unreachable`
- **Клиент:** `Services/Dashboard/Clients/RetentionClient.MarkStorageUnreachableAsync`
- **Retry:** Polly, 3 попытки (1/2/4 с)
- **Ожидаемые ответы:** `204 No Content`, `403 Forbidden` (хотя бы одна запись принадлежит чужому агенту), `401 Unauthorized`

### Тело запроса (`MarkStorageUnreachableDto`)

```json
{
  "ids": [
    "0f4c1c1e-7b8f-4e9a-9f2b-1b3a4c5d6e7f",
    "1a2b3c4d-5e6f-7081-9293-a4b5c6d7e8f9"
  ]
}
```

| Поле  | Тип      | Описание                                                                       |
|-------|----------|--------------------------------------------------------------------------------|
| `ids` | Guid[]   | ID записей, для которых агент не смог разрешить `storageName` через `StorageResolver` |

---

## Итоговая сводка: где формируется payload

| Запрос                          | Код, который строит тело                                     |
|---------------------------------|--------------------------------------------------------------|
| `POST /backup-record`           | `Services/Dashboard/Clients/BackupRecordClient.OpenAsync`            |
| `POST /backup-record/{id}/progress` | `Services/Common/ProgressReporterFactory.ToDto` → `BackupRecordClient.ReportProgressAsync` |
| `PATCH /backup-record/{id}`     | `Services/Backup/BackupJob.BuildFinalizeDto` → `BackupRecordClient.FinalizeAsync` |
| `GET /schedule`                 | —                                                            |
| `POST /connections`             | `Services/Dashboard/Sync/ConnectionSyncService.BuildPayload`      |
| `POST /databases`               | `Services/Dashboard/Sync/DatabaseSyncService.BuildPayload`        |
| `POST /filesets`                | `Services/Dashboard/Sync/FileSetSyncService.BuildPayload`         |
| `GET /task`                     | —                                                            |
| `POST /task/{id}/progress`      | `Services/Common/ProgressReporterFactory.ToTaskDto` → `AgentTaskClient.ReportProgressAsync` |
| `PATCH /task/{id}`              | `Workers/Handlers/{Restore,Delete,Backup}TaskHandler.HandleAsync` (выбираются через `AgentTaskPollingService.DispatchAsync` по `task.Type`) → `AgentTaskClient.PatchTaskAsync` |
| `GET /backup-records/expired`   | —                                                                                       |
| `DELETE /backup-records/{id}`   | —                                                                                       |
| `POST /backup-records/mark-unreachable` | `Workers/RetentionWorker` (собирает список нерезолвнутых) → `RetentionClient.MarkStorageUnreachableAsync` |

Если вы меняете что-то в этих файлах — обновите и этот документ в том же PR.

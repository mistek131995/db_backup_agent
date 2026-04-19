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

- `BackupRecordClient`, `ScheduleService`, `ConnectionSyncService` — **20 с** на вызов.
- `RestoreTaskClient` — **60 с** на вызов (покрывает long-poll 30 с с запасом).
- Progress-вызовы (`BackupRecordClient.ReportProgressAsync`, `RestoreTaskClient.ReportProgressAsync`) поверх этого дополнительно ограничены `CancellationTokenSource.CancelAfter(3 с)` — heartbeat не должен ни при каких условиях задерживать пайплайн.

Polly-ретраи (1/2/4 с) срабатывают поверх этих лимитов. Общее время на одну операцию с ретраями — `таймаут × 3 + 7 с` в худшем случае.

### Что никогда не уходит на дашборд

- `Connections[].Username`, `Connections[].Password`
- `EncryptionSettings.Key`
- `Storages[].S3.AccessKey`, `Storages[].S3.SecretKey`
- `Storages[].Sftp.Password`, `Storages[].Sftp.PrivateKeyPath`, `Storages[].Sftp.PrivateKeyPassphrase`
- Содержимое дампов, чанков, файлов (весь payload бэкапа шифруется AES-256-GCM и идёт напрямую в ваш S3/SFTP, минуя дашборд)

Если вы нашли в выхлопе агента или в трафике что-то из этого списка — это баг. Пишите в репозиторий.

---

## 1. Backup — открыть запись

Первый шаг бэкап-пайплайна. Агент просит дашборд завести запись и вернуть `id`, чтобы дальше слать прогресс и финализацию по этому id.

- **Метод / URL:** `POST {DashboardUrl}/api/v1/agent/backup-record`
- **Клиент:** `Services/Dashboard/BackupRecordClient.OpenAsync`
- **Retry:** Polly, 3 попытки (1/2/4 с)
- **Ожидаемые ответы:** `201 Created` c телом, `404 Not Found` (БД не зарегистрирована у агента), `401 Unauthorized`

### Тело запроса (`OpenBackupRecordDto`)

```json
{
  "databaseName": "mydb",
  "connectionName": "main-pg"
}
```

| Поле             | Тип    | Описание                                                              |
|------------------|--------|-----------------------------------------------------------------------|
| `databaseName`   | string | Имя БД из `Databases[].Database`                                      |
| `connectionName` | string | Имя подключения из `Databases[].ConnectionName` (должно быть в `Connections[]`) |

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
- **Клиент:** `Services/Dashboard/BackupRecordClient.ReportProgressAsync`
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
- **Клиент:** `Services/Dashboard/BackupRecordClient.FinalizeAsync`
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
- **Клиент:** `Services/Dashboard/ScheduleService`
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
| `overrides`      | `ScheduleOverrideDto[]?`   | Per-database override'ы. Применяется, если `isActive=true`    |

---

## 5. Connection sync

Отправляется один раз на старте (и после успеха — останавливается). Повторные попытки внешние: backoff 10 с → 5 мин до первого успеха.

- **Метод / URL:** `POST {DashboardUrl}/api/v1/agent/connections`
- **Клиент:** `Services/Dashboard/ConnectionSyncService`
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

## 6. Restore — long-poll задачи

Агент опрашивает дашборд каждые 30 секунд (long-poll). Первый запрос сразу после старта; после выполнения задачи — сразу следующий; при 5xx/сетевых ошибках backoff 10 с → 5 мин.

- **Метод / URL:** `GET {DashboardUrl}/api/v1/agent/restore-task`
- **Клиент:** `Services/Dashboard/RestoreTaskClient.FetchTaskAsync`
- **Тело запроса:** пустое
- **Ожидаемые ответы:** `204 No Content` (задач нет), `200 OK` c телом, `401 Unauthorized`

### Тело ответа (`RestoreTaskForAgentDto`)

```json
{
  "taskId": "7a2e1d8f-9b4c-4a1f-b5c6-1d2e3f4a5b6c",
  "sourceDatabaseName": "mydb",
  "dumpObjectKey": "mydb/2026-04-18_03-00-00/dump.sql.gz.enc",
  "targetDatabaseName": "mydb_restore",
  "manifestKey": "mydb/2026-04-18_03-00-00/manifest.json.enc",
  "targetFileRoot": "/var/data/myapp",
  "targetConnectionName": "main-pg",
  "storageName": "prod-s3"
}
```

| Поле                   | Тип     | Описание                                                                                           |
|------------------------|---------|----------------------------------------------------------------------------------------------------|
| `taskId`               | Guid    | ID задачи; используется во всех последующих запросах                                               |
| `sourceDatabaseName`   | string  | Имя исходной БД (для резолва `DatabaseConfig`, если `storageName` не пришёл)                       |
| `dumpObjectKey`        | string  | Ключ дампа в хранилище                                                                             |
| `targetDatabaseName`   | string? | Куда восстановить БД. `null` = тот же `sourceDatabaseName`                                         |
| `manifestKey`          | string? | Ключ манифеста файлов. `null` = файловой части нет, восстанавливаем только БД                      |
| `targetFileRoot`       | string? | Куда класть файлы. `null` = в служебную папку агента (`RestoreSettings.FileRestoreBasePath`, дефолт `restore-files/`); она очищается перед каждым restore и доступна только на хосте агента |
| `targetConnectionName` | string? | Override подключения. `null` = подключение из `DatabaseConfig` исходной БД                         |
| `storageName`          | string? | Override хранилища. `null` = хранилище из `DatabaseConfig` исходной БД                             |

---

## 7. Restore — прогресс

Heartbeat задачи восстановления. Шлётся не чаще раза в 5 секунд + немедленно при смене стадии. Ошибки swallow-ятся.

- **Метод / URL:** `POST {DashboardUrl}/api/v1/agent/restore-task/{id}/progress`
- **Клиент:** `Services/Dashboard/RestoreTaskClient.ReportProgressAsync`
- **Retry:** нет. Таймаут 3 с
- **Ожидаемые ответы:** `204 No Content`, `403 Forbidden`, `409 Conflict` (задача уже не `InProgress`)

### Тело запроса (`RestoreProgressDto`)

```json
{
  "stage": "restoringFiles",
  "processed": 84,
  "total": 142,
  "unit": "files",
  "currentItem": "/var/data/myapp/assets/logo.png"
}
```

| Поле          | Тип           | Описание                                                                                                                                                            |
|---------------|---------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `stage`       | enum (string) | Одно из: `downloadingDump`, `decryptingDump`, `decompressingDump`, `preparingDatabase`, `restoringDatabase`, `downloadingManifest`, `restoringFiles`                |
| `processed`   | long?         | Обработано единиц                                                                                                                                                   |
| `total`       | long?         | Всего единиц                                                                                                                                                        |
| `unit`        | string?       | `"bytes"` или `"files"`                                                                                                                                             |
| `currentItem` | string?       | Имя текущего файла на стадии `restoringFiles` (иначе `null`)                                                                                                        |

---

## 8. Restore — финализация

Закрывает задачу финальным статусом. Идемпотентно.

- **Метод / URL:** `PATCH {DashboardUrl}/api/v1/agent/restore-task/{id}`
- **Клиент:** `Services/Dashboard/RestoreTaskClient.PatchTaskAsync`
- **Retry:** Polly, 3 попытки (1/2/4 с)
- **Ожидаемые ответы:** `204 No Content`, `400 Bad Request`, `403 Forbidden`

### Тело запроса (`PatchRestoreTaskDto`)

```json
{
  "status": "partial",
  "databaseStatus": "success",
  "filesStatus": "partial",
  "errorMessage": "2 файла не удалось восстановить: permission denied ...",
  "filesRestoredCount": 140,
  "filesFailedCount": 2
}
```

| Поле                 | Тип           | Описание                                                                                                      |
|----------------------|---------------|---------------------------------------------------------------------------------------------------------------|
| `status`             | enum (string) | `success`, `failed`, `partial`                                                                                |
| `databaseStatus`     | enum (string) | `success`, `failed`. `null` — задача не затрагивала БД                                                        |
| `filesStatus`        | enum (string) | `success`, `failed`, `partial`, `skipped`. `null` — задача не затрагивала файлы                               |
| `errorMessage`       | string?       | Человекочитаемое сообщение. Для `partial` — список первых 20 ошибок, обрезанный до 2000 символов              |
| `filesRestoredCount` | int?          | Сколько файлов успешно восстановилось                                                                         |
| `filesFailedCount`   | int?          | Сколько файлов не удалось восстановить                                                                        |

---

## Итоговая сводка: где формируется payload

| Запрос                          | Код, который строит тело                                     |
|---------------------------------|--------------------------------------------------------------|
| `POST /backup-record`           | `Services/Dashboard/BackupRecordClient.OpenAsync`            |
| `POST /backup-record/{id}/progress` | `Services/Common/ProgressReporterFactory.ToDto` → `BackupRecordClient.ReportProgressAsync` |
| `PATCH /backup-record/{id}`     | `Services/Backup/BackupJob.BuildFinalizeDto` → `BackupRecordClient.FinalizeAsync` |
| `GET /schedule`                 | —                                                            |
| `POST /connections`             | `Services/Dashboard/ConnectionSyncService.BuildPayload`      |
| `GET /restore-task`             | —                                                            |
| `POST /restore-task/{id}/progress` | `Services/Common/ProgressReporterFactory.ToDto` → `RestoreTaskClient.ReportProgressAsync` |
| `PATCH /restore-task/{id}`      | `Workers/RestoreTaskPollingService.CombineResults` → `RestoreTaskClient.PatchTaskAsync` |

Если вы меняете что-то в этих файлах — обновите и этот документ в том же PR.

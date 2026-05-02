# Восстановление

Восстановление полностью реализовано и запускается из интерфейса дашборда. На стороне агента поведение определяется двумя местами: рабочим каталогом и правами target-подключения.

- [Как работает](#как-работает)
- [`RestoreSettings` — рабочий каталог](#restoresettings--рабочий-каталог)
- [Требуемые права target-подключения](#требуемые-права-target-подключения)
- [Поведение при восстановлении](#поведение-при-восстановлении)

---

## Как работает

```
Poll task → Download → Decrypt → Restore DB → Restore Files → Report
```

1. **Long-poll** — запрашивает `GET /api/v1/agent/task` (единый канал задач). При отсутствии задачи — ждёт 30 секунд; при получении — сразу исполняет и тут же опрашивает снова.
2. **Database restore** — проверяет права target-подключения одним SQL-запросом до скачивания дампа → скачивает и расшифровывает → разворачивает БД одним из 5 способов в зависимости от `DatabaseType` × `BackupMode` → cleanup временных файлов:
   - **Postgres logical** — `pg_terminate_backend` → `DROP DATABASE IF EXISTS` → `CREATE DATABASE` → `psql -f -v ON_ERROR_STOP=1`.
   - **Postgres physical** — `pg_ctl stop` → атомарный swap PGDATA (`Directory.Move`) → `pg_ctl start`. Восстанавливается **весь кластер целиком**, не одна БД. Требует service-manager guard'а и доступа к PGDATA — см. [postgres.md](postgres.md).
   - **MySQL logical** — `DROP DATABASE IF EXISTS` → `CREATE DATABASE` → `mysql` поверх stdin со стримом `gzip` (без промежуточного `.sql`).
   - **MSSQL logical** — `DROP DATABASE IF EXISTS` → `DacServices.ImportBacpac` по TDS (in-process, без внешних бинарников).
   - **MSSQL physical** — `DROP DATABASE IF EXISTS` → `RESTORE DATABASE ... WITH FILE = 1, REPLACE, RECOVERY` + автоматические `MOVE`-клозы для логических имён файлов.

   Если file-set-бэкап (`DumpObjectKey` пустой) — этот шаг пропускается полностью, выполняется только пункт 3.
3. **File restore** — если в бэкапе были файлы (`ManifestKey`), скачивает зашифрованный манифест, поштучно собирает каждый файл из чанков, атомарно переименовывает `.restore-tmp` → target. Падение одного файла не валит задачу — статус `partial` с подробностями.
4. **Report** — `PATCH /api/v1/agent/task/{id}` с финальным статусом (`success` / `failed` / `partial`).

**Backup и restore на одном агенте не идут параллельно** — координация через общий lock. Запуск по расписанию ждёт, если идёт восстановление, и наоборот.

---

## `RestoreSettings` — рабочий каталог

```json
"RestoreSettings": {
  "TempPath": "/mnt/restore-temp",
  "FileRestoreBasePath": "/mnt/restored-files"
}
```

Оба поля опциональны и в шаблон `appsettings.json` не входят.

### `TempPath`

Куда агент скачивает и расшифровывает дамп перед передачей в `psql`/`sqlcmd`. Каждая задача пишет в свой subdir `{TempPath}/{taskId}/`, cleanup в `try/finally` даже при ошибке. Тот же `TempPath` использует стриминговый reader/writer манифеста (subdir `manifest-r-{guid}` / `manifest-w-{guid}`): во время бэкапа нужен запас под несжатый `.gz` + зашифрованный `.gz.enc` (ориентировочно ~60 байт × число файлов × 2), во время рестора и GC — только распакованный `.gz`. Все temp-файлы удаляются при `DisposeAsync`.

- **По умолчанию поле не задаётся** — и шаблон `appsettings.json` его не содержит. В этом случае используется хардкод `./temp`, который резолвится относительно директории исполняемого файла (**не** текущего каталога процесса — это важно для Windows-службы, у которой CWD по умолчанию `C:\Windows\System32`).
- **Абсолютный путь** используется как есть. Полезно, если нужно выделить под temp отдельный volume с достаточным местом под многогигабайтные дампы.
- **Для MSSQL** финальный `.bak` кладётся не в `TempPath`, а в `AgentBackupPath ?? SharedBackupPath` — это требование SQL Server (см. [mssql.md](mssql.md)).

### `FileRestoreBasePath`

Служебная landing-директория для восстановления файлов, когда задача пришла **без** `targetFileRoot`. Перед каждым restore очищается полностью. Доступна только на хосте агента — ни дашборд, ни оператор через UI её содержимое не видят.

- **По умолчанию не задано** — используется хардкод `./restore-files`, который резолвится относительно директории исполняемого файла (та же логика, что у `TempPath`).
- **Абсолютный путь** используется как есть.
- Если оператор в задаче restore указал `targetFileRoot` — файлы кладутся туда, а `FileRestoreBasePath` не трогается.

---

## Требуемые права target-подключения

Агент проверяет права **до** скачивания дампа — чтобы не тратить трафик на задачу, которая в конце упадёт на `permission denied`. Если прав не хватает, в статусе задачи на дашборде будет подробное сообщение с готовой SQL-командой для выдачи прав.

**PostgreSQL** — нужно одно из:
- `rolsuper = true` (superuser), **или**
- `rolcreatedb = true` (для `DROP`/`CREATE DATABASE`) **и** членство в `pg_signal_backend` (для отключения активных сессий).

```sql
ALTER ROLE "restore_user" WITH CREATEDB;
GRANT pg_signal_backend TO "restore_user";
```

Для **physical**-режима дополнительно нужен `REPLICATION` для `pg_basebackup` и доступ к PGDATA на хосте — подробности в [postgres.md](postgres.md).

**MSSQL** — два независимых условия (одно для CREATE, второе для DROP существующей):

- **Создание target-БД:** `sysadmin` (server-level) или `dbcreator` (server-level).
- **DROP существующей** target-БД: `sysadmin`, владение БД (`db_owner`), либо `CONTROL` permission. Если target ещё не существует — снимается автоматически.

```sql
-- Право на создание новых БД
ALTER SERVER ROLE dbcreator ADD MEMBER [restore_user];

-- Право на удаление существующей target (один из вариантов)
USE [mydb];
ALTER ROLE db_owner ADD MEMBER restore_user;
-- или явно:
-- GRANT CONTROL ON DATABASE::[mydb] TO restore_user;
```

**MySQL** — пользователь должен иметь права на `CREATE`/`DROP DATABASE` для target, плюс `INSERT`/`CREATE`/`DROP`/`ALTER`/`INDEX` на объектах внутри. Минимум — `ALL PRIVILEGES` на конкретную БД (на случай произвольного содержимого дампа):

```sql
GRANT ALL PRIVILEGES ON `mydb`.* TO 'restore_user'@'%';
GRANT CREATE, DROP ON *.* TO 'restore_user'@'%';
FLUSH PRIVILEGES;
```

---

## Поведение при восстановлении

- **Всё или ничего внутри DB-бэкапа.** Если source — это DB-бэкап и в нём были файлы (`ManifestKey != null`), они восстанавливаются всегда вместе с БД. Селективный режим «только БД» или «только файлы» внутри DB-бэкапа не поддерживается — привело бы к рассинхрону между таблицами и файловой системой.
- **File-set-бэкапы восстанавливаются как только файлы.** У file-set-записи `DumpObjectKey = null`, поэтому DB-этап автоматически пропускается (`RestoreTaskHandler` ставит `DatabaseRestoreResult.Success()` и идёт сразу к file-restore). Это не специальный режим, а естественное поведение для записей без дампа.
- **Target БД перезаписывается.** Перед восстановлением агент делает `DROP DATABASE IF EXISTS` (Postgres) или `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` + `DROP DATABASE` (MSSQL). Активные соединения принудительно закрываются.
- **Файлы перезаписываются.** Каждый файл собирается в `.restore-tmp` и атомарно переименовывается. Ошибка на одном файле → статус задачи `partial`, список упавших файлов — в `ErrorMessage`.
- **Кросс-платформа пока не поддерживается.** Windows-бэкап на Linux-агент (или наоборот) может не заработать из-за особенностей путей и прав. Восстанавливайте на ту же ОС, что делала бэкап.

# Установка и запуск

- [Требования](#требования)
- [Docker](#docker)
- [Linux (systemd)](#linux-systemd)
- [Windows (служба)](#windows-служба)
- [Для разработки](#для-разработки)
- [Поведение при пустом конфиге](#поведение-при-пустом-конфиге)

---

## Требования

- .NET 10 Runtime (или SDK для сборки из исходников)
- `pg_dump` / `psql` в `PATH` — для PostgreSQL (backup + restore)
- `mysqldump` / `mysql` в `PATH` — для MySQL/MariaDB (backup + restore)
- `sqlcmd` в `PATH` — для MSSQL (backup + restore)
- Зарегистрированный агент на [backupster.io](https://backupster.io/) (нужен токен)

---

## Docker

```bash
docker run -d --name backupster-agent \
  --restart unless-stopped \
  -e AgentSettings__Token=<токен> \
  -e AgentSettings__DashboardUrl=<url дашборда> \
  -v /root/backupster-agent:/app/config \
  ghcr.io/mistek131995/backupster-agent:latest
```

Volume `/root/backupster-agent:/app/config` сохраняет конфиг, расписание запусков (`runs/`) и очередь offline-бэкапов (`outbox/`). Без него данные пропадут при пересоздании контейнера.

Если планируете использовать файловый бэкап (`FilePaths`), смонтируйте исходные директории в контейнер отдельными томами и пропишите их контейнерные пути в `FilePaths`:

```bash
docker run -d --name backupster-agent \
  --restart unless-stopped \
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

---

## Linux (systemd)

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

При первом запуске агент создаст шаблон `/opt/backupster-agent/config/appsettings.json`. Заполните его и перезапустите службу:

```bash
sudo systemctl restart backupster-agent
```

---

## Windows (служба)

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

---

## Для разработки

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

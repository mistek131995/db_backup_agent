#!/bin/sh
set -e

ACTION="$1"

case "$ACTION" in
    configure)
        if [ -z "$2" ]; then
            FRESH=1
        else
            FRESH=0
        fi
        ;;
    1)
        FRESH=1
        ;;
    2)
        FRESH=0
        ;;
    *)
        FRESH=0
        ;;
esac

NOLOGIN_SHELL=/bin/false
for candidate in /usr/sbin/nologin /sbin/nologin; do
    if [ -x "$candidate" ]; then
        NOLOGIN_SHELL="$candidate"
        break
    fi
done

if ! getent group backupster >/dev/null; then
    groupadd --system backupster
fi

if ! getent passwd backupster >/dev/null; then
    useradd \
        --system \
        --gid backupster \
        --home-dir /var/lib/backupster-agent \
        --shell "$NOLOGIN_SHELL" \
        --comment "Backupster Agent" \
        backupster
fi

install -d -m 0750 -o backupster -g backupster /var/lib/backupster-agent
install -d -m 0750 -o backupster -g backupster /var/lib/backupster-agent/config
install -d -m 0750 -o backupster -g backupster /var/lib/backupster-agent/outbox
install -d -m 0750 -o backupster -g backupster /var/lib/backupster-agent/runs
install -d -m 0750 -o backupster -g backupster /var/lib/backupster-agent/temp

if [ -f /etc/backupster-agent/env ]; then
    chown root:backupster /etc/backupster-agent/env
    chmod 0640 /etc/backupster-agent/env
fi

systemctl daemon-reload >/dev/null 2>&1 || true

if [ "$FRESH" = "1" ]; then
    systemctl enable backupster-agent.service >/dev/null 2>&1 || true
    cat <<MSG

Backupster Agent installed.

  1. Edit /etc/backupster-agent/env and set AgentSettings__Token.
  2. Start the service:
       systemctl start backupster-agent
  3. Check status:
       systemctl status backupster-agent
       journalctl -u backupster-agent -f

MSG
else
    systemctl try-restart backupster-agent.service >/dev/null 2>&1 || true
fi

exit 0

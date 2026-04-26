#!/bin/sh
set -e

ACTION="$1"

case "$ACTION" in
    purge)
        rm -rf /etc/backupster-agent
        rm -rf /var/lib/backupster-agent
        if getent passwd backupster >/dev/null; then
            userdel backupster >/dev/null 2>&1 || true
        fi
        ;;
esac

systemctl daemon-reload >/dev/null 2>&1 || true

exit 0

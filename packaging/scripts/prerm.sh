#!/bin/sh
set -e

ACTION="$1"

case "$ACTION" in
    remove|0)
        systemctl stop backupster-agent.service >/dev/null 2>&1 || true
        systemctl disable backupster-agent.service >/dev/null 2>&1 || true
        ;;
esac

exit 0

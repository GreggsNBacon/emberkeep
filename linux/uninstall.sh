#!/bin/sh
# Removes the Emberkeep service and program. Settings, game servers and backups in /var/lib/emberkeep stay,
# unless you add --purge (which also removes the emberkeep user).
# Usage: sudo ./uninstall.sh [--purge]
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this with sudo: sudo ./uninstall.sh" >&2
  exit 1
fi

if systemctl list-unit-files emberkeep.service >/dev/null 2>&1; then
  echo "Stopping Emberkeep (running servers are stopped properly first)…"
  systemctl disable --now emberkeep 2>/dev/null || true
fi
rm -f /etc/systemd/system/emberkeep.service /usr/local/bin/emberkeep-link
systemctl daemon-reload 2>/dev/null || true
rm -rf /opt/emberkeep

if [ "${1:-}" = "--purge" ]; then
  userdel -r emberkeep 2>/dev/null || rm -rf /var/lib/emberkeep
  echo "Emberkeep, its settings and everything in /var/lib/emberkeep were removed."
else
  echo "Emberkeep was removed. Your settings, game servers and backups are still in /var/lib/emberkeep."
  echo "To remove those too: sudo ./uninstall.sh --purge"
fi

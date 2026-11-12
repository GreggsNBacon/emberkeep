#!/bin/sh
# Installs (or updates) Emberkeep as a systemd service:
#   /opt/emberkeep/emberkeep              the program
#   /var/lib/emberkeep                    home of the "emberkeep" user: settings, game servers, SteamCMD, backups
#   /etc/systemd/system/emberkeep.service the service (starts with the machine)
#   /usr/local/bin/emberkeep              the emberkeep command (emberkeep link, emberkeep open)
#   /usr/share/applications               an "Emberkeep" menu entry, on machines with a desktop
# Usage: sudo ./install.sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this with sudo: sudo ./install.sh" >&2
  exit 1
fi
if ! command -v systemctl >/dev/null 2>&1; then
  echo "This installer needs systemd. Without it, run ./emberkeep directly (as a normal user)." >&2
  exit 1
fi

HERE=$(cd "$(dirname "$0")" && pwd)
PREFIX=/opt/emberkeep
SVC_USER=emberkeep
HOME_DIR=/var/lib/emberkeep

if ! id "$SVC_USER" >/dev/null 2>&1; then
  NOLOGIN=$(command -v nologin || echo /usr/sbin/nologin)
  useradd --system --user-group --create-home --home-dir "$HOME_DIR" --shell "$NOLOGIN" "$SVC_USER"
  echo "Created the $SVC_USER user (home: $HOME_DIR)."
fi
install -d -o "$SVC_USER" -g "$SVC_USER" -m 755 "$PREFIX"
# Settings, game servers and the sign-in link: the emberkeep user, plus the people in its group.
install -d -o "$SVC_USER" -g "$SVC_USER" -m 750 "$HOME_DIR"
chmod 750 "$HOME_DIR"
if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ] && ! id -nG "$SUDO_USER" | grep -qw "$SVC_USER"; then
  usermod -aG "$SVC_USER" "$SUDO_USER"
  echo "Added $SUDO_USER to the $SVC_USER group, so you can open Emberkeep without sudo (from your next login)."
fi

if systemctl is-active --quiet emberkeep 2>/dev/null; then
  echo "Stopping Emberkeep (running servers are stopped properly first)…"
  systemctl stop emberkeep
fi

# The program folder belongs to the service user, so Emberkeep can install its own updates.
install -m 755 -o "$SVC_USER" -g "$SVC_USER" "$HERE/emberkeep" "$PREFIX/emberkeep"
install -m 644 "$HERE/emberkeep.service" /etc/systemd/system/emberkeep.service
rm -f /usr/local/bin/emberkeep-link # older versions' helper
ln -sf "$PREFIX/emberkeep" /usr/local/bin/emberkeep
if [ -d /usr/share/applications ]; then
  install -m 644 "$HERE/emberkeep.desktop" /usr/share/applications/emberkeep.desktop
  install -D -m 644 "$HERE/emberkeep.svg" /usr/share/icons/hicolor/scalable/apps/emberkeep.svg
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
fi

systemctl daemon-reload
systemctl enable --now emberkeep >/dev/null 2>&1 || systemctl enable --now emberkeep

LINK_FILE="$HOME_DIR/.config/Emberkeep/sign-in-link.txt"
i=0
while [ ! -f "$LINK_FILE" ] && [ $i -lt 40 ]; do sleep 0.5; i=$((i + 1)); done

echo
if [ -f "$LINK_FILE" ]; then
  LINK=$(cat "$LINK_FILE")
  PORT=$(echo "$LINK" | sed -n 's#^http://127\.0\.0\.1:\([0-9]*\)/.*#\1#p')
  echo "Emberkeep is running."
  echo
  echo "On this computer, open Emberkeep from your apps menu. Or sign in as the host with this link"
  echo "(it changes when Emberkeep restarts; get it again with: sudo emberkeep link):"
  echo "  $LINK"
  echo
  echo "On another computer, forward the port over SSH first, then open the link there:"
  echo "  ssh -L ${PORT:-8765}:127.0.0.1:${PORT:-8765} $(logname 2>/dev/null || echo you)@$(hostname)"
  echo
  echo "Then, in App settings: create accounts (Users & groups) and turn on Allow remote access,"
  echo "so people can sign in from their browsers at http://<this server>:${PORT:-8765}."
else
  echo "Emberkeep was installed but hasn't started yet. See: sudo journalctl -u emberkeep -e"
fi

# SteamCMD (used to install game servers) is a 32-bit program.
if command -v dpkg >/dev/null 2>&1 && ! dpkg -s lib32gcc-s1 >/dev/null 2>&1; then
  echo
  echo "To install game servers with SteamCMD, also install its 32-bit libraries:"
  echo "  sudo dpkg --add-architecture i386 && sudo apt update && sudo apt install lib32gcc-s1"
elif command -v rpm >/dev/null 2>&1 && ! rpm -q glibc.i686 >/dev/null 2>&1; then
  echo
  echo "To install game servers with SteamCMD, also install its 32-bit libraries:"
  echo "  sudo dnf install glibc.i686 libstdc++.i686"
fi

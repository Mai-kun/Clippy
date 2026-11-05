#!/usr/bin/env bash
# Removes Clippy: binaries, autostart, shortcuts. User data survives.
set -euo pipefail

APP_DIR="$HOME/.local/share/Clippy"

systemctl --user disable --now clippy.service 2>/dev/null || true
rm -f "$HOME/.config/systemd/user/clippy.service"
systemctl --user daemon-reload 2>/dev/null || true
rm -f "$HOME/.config/autostart/clippy.desktop"
rm -f "$HOME/.local/share/applications/clippy.desktop"
rm -f "$HOME/.local/bin/clippy"
rm -f "$APP_DIR/Clippy" "$APP_DIR/uninstall.sh" "$APP_DIR/config.example.json"
rmdir "$APP_DIR" 2>/dev/null || true

echo "Clippy uninstalled (binaries, autostart, shortcuts removed)."
echo "Kept untouched: clips, config.json, portal_token.txt, logs -- under $APP_DIR (and the menu icon in ~/.local/share/icons)."

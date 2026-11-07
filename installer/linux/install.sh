#!/usr/bin/env bash
# Clippy for Linux -- user-local install (no sudo).
#
# Packs into Clippy-linux-x64.tar.gz next to the binary; installs into
# ~/.local/share/Clippy, links ~/.local/bin/clippy, registers the desktop
# entry and the systemd --user autostart. Existing config.json,
# portal_token.txt and clips/ are preserved: updating is an in-place copy.
set -euo pipefail

APP_DIR="$HOME/.local/share/Clippy"
BIN_DIR="$HOME/.local/bin"
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ ! -x "$SRC_DIR/Clippy" ]; then
    echo "error: $SRC_DIR/Clippy not found -- run this script from the archive root." >&2
    exit 1
fi

mkdir -p "$APP_DIR" "$BIN_DIR"

# The binary and the shipped files. User data (config.json, portal_token.txt,
# clips/, logs/) is never touched: an update is a copy over it.
install -m 755 "$SRC_DIR/Clippy" "$APP_DIR/Clippy"
[ -f "$SRC_DIR/config.example.json" ] && install -m 644 "$SRC_DIR/config.example.json" "$APP_DIR/config.example.json" || true
[ -f "$SRC_DIR/uninstall.sh" ] && install -m 755 "$SRC_DIR/uninstall.sh" "$APP_DIR/uninstall.sh" || true

# ~/.local/bin/clippy -- the form compositor shortcuts and terminal use.
ln -sfn "$APP_DIR/Clippy" "$BIN_DIR/clippy"
case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) echo "note: $BIN_DIR is not on PATH -- add it to use 'clippy' from a terminal." ;;
esac

# Menu entry (the app itself generates the icon into the hicolor theme on
# its first run and rewrites this file with the exact binary path).
mkdir -p "$HOME/.local/share/applications"
cat > "$HOME/.local/share/applications/clippy.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Clippy
Comment=Instant replay recorder
Exec=$APP_DIR/Clippy
Icon=clippy
Terminal=false
Categories=Utility;AudioVideo;Recorder;
DESKTOP

# Autostart: systemd --user when the session has one, XDG autostart otherwise.
# is-system-running exits non-zero for "degraded" too, and with pipefail a plain
# `... | grep -q degraded` would eat the match -- so the state is captured first.
SYSTEMD_STATE="$(systemctl --user is-system-running 2>/dev/null || true)"
if [ "$SYSTEMD_STATE" = "running" ] || [ "$SYSTEMD_STATE" = "degraded" ]; then
    mkdir -p "$HOME/.config/systemd/user"
    cat > "$HOME/.config/systemd/user/clippy.service" <<SERVICE
[Unit]
Description=Clippy Instant Replay Recorder
# Ordering AFTER the portal pair: xdg-desktop-portal elects its backends once at
# startup, and a recorder starting too early would pull it up half-assembled --
# without the ScreenCast interface -- and keep it that way for the whole session.
After=graphical-session.target pipewire.service pipewire-pulse.service xdg-desktop-portal.service plasma-xdg-desktop-portal-kde.service
PartOf=graphical-session.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/Clippy
Restart=on-failure
RestartSec=3

[Install]
WantedBy=graphical-session.target default.target
SERVICE
    systemctl --user daemon-reload
    if pgrep -x Clippy >/dev/null 2>&1; then
        # An instance is already running (an update over a live session): enable
        # without --now, or two recorders would race for the portal.
        systemctl --user enable clippy.service
        echo "Clippy is already running -- autostart enabled without starting a second copy."
    else
        systemctl --user enable --now clippy.service
        echo "Autostart: systemd --user clippy.service enabled and started."
    fi
else
    mkdir -p "$HOME/.config/autostart"
    cat > "$HOME/.config/autostart/clippy.desktop" <<AUTOSTART
[Desktop Entry]
Type=Application
Name=Clippy
Exec=$APP_DIR/Clippy
Icon=clippy
Terminal=false
X-GNOME-Autostart-enabled=true
AUTOSTART
    echo "systemd --user is unavailable -- XDG autostart entry created instead."
fi

# Global hotkeys through evdev need the input group; without it the recorder
# still works, just without direct F8/F9 capture.
if ! id -nG "$USER" | grep -qw input; then
    cat <<HINT

NOTE: for the global F8/F9 hotkeys through evdev, add yourself to the input group:
    sudo usermod -aG input \$USER
...and log out and back in. Alternatively, bind your compositor shortcuts
(KDE Plasma: System Settings > Shortcuts; Niri: config.kdl) to:
    clippy --save-short
    clippy --save-long
HINT
fi

echo
echo "Installed: $APP_DIR"
echo "Launch:    'Clippy' from the application menu, or 'clippy' from a terminal."

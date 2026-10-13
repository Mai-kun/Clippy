# 📎 Clippy

**English | [Русский](README_RU.md)**

> **A featherweight, no-compromise instant replay recorder for Windows.**  
> A ShadowPlay and Medal replacement with none of the Electron bloat, none of the mandatory logins, and none of the background CPU tax.

[![GitHub Release](https://img.shields.io/github/v/release/Mai-kun/Clippy?style=flat-square&color=blue)](https://github.com/Mai-kun/Clippy/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-informational?style=flat-square)]()
[![Build](https://img.shields.io/badge/Runtime-Native%20AOT%20(.NET%2010)-purple?style=flat-square)]()

---

### 🎯 What makes Clippy different?

Most modern game recorders (Medal, Outplayed) are built on Electron: 300–500 MB installers, 1.5–2 GB of RAM, and precious frames stolen from your games. ShadowPlay drags in a separate NVIDIA stack, a forced login, and a pile of telemetry services.

**Clippy attacks the problem from the systems side:**

* 🚀 **0.2% CPU and 0-copy VRAM:** the desktop frame captured via `Windows.Graphics.Capture` goes straight into the NVENC chip in video-card memory — no hundreds of MB/s pumped through the CPU and PCIe.
* 📦 **Tiny footprint (~8 MB):** a fully native executable (Native AOT) with no .NET Runtime requirement. The release archive weighs about 4 MB.
* 🧠 **Two-tier hybrid buffer:** the freshest 32 seconds live in RAM, while the long 3-minute tail spills to an SSD cache — process memory stays pinned at ~50–120 MB.
* 🎬 **Hardware HEVC (H.265) out of the box:** a three-minute clip in great quality weighs only ~60 MB.
* 🎧 **Reference-grade A/V sync:** audio latency locked at -15…-20 ms with exactly 0.0 ms of drift at any recording length.
* 🪟 **Clean background start:** no flickering black console windows — Clippy boots straight into the system tray, runs on hotkeys (F8 / F9) and shows native Windows toasts.

---

## Quick start

Two simple ways to install:

### 1. Installer (`Clippy-Setup.exe`)
Grab `Clippy-Setup.exe` from the [releases page](https://github.com/Mai-kun/Clippy/releases). No admin rights needed: it installs to `AppData\Local\Programs\Clippy`, adds a Start menu shortcut, offers optional startup with Windows and supports one-click silent updates.

### 2. Portable (`Clippy-win-x64.zip`)
Unzip anywhere and run `Clippy.exe`.

> `ffmpeg.exe` is **not** required: direct access to the NVENC chip (`clippy_nvenc.dll`) plus Windows Media Foundation for system-audio encoding make Clippy fully self-contained. If you ever need an ffmpeg fallback (e.g. AMD/Intel GPUs), drop `ffmpeg.exe` next to the exe or put it on your PATH.

Once started, Clippy quietly appears in the system tray next to the clock. Launch any game — hit **F8** right after a spectacular moment to save the last 30 seconds, or **F9** for the last 3 minutes.

To quit: right-click the tray icon → **Exit**.

---

### Hotkeys

| Key | Action | Default duration |
|---|---|---|
| **F8** | save a short clip | 30 seconds |
| **F9** | save a long clip | 3 minutes |

Both keys are rebindable right from the tray context menu (F1–F12, NumPad 0–9) or via `config.json`. Hotkeys are global and work over any fullscreen game.

After a clip is saved, Clippy plays a short system beep in your headphones and shows a save notification: by default a compact dark banner in the top-right corner in Medal / ShadowPlay style — it doesn't steal focus or swallow clicks (never interrupts the game), fades in, stays for a couple of seconds and softly fades out showing the saved file name. Windows' own notification chime is muted, so you only hear Clippy's clean built-in sound. The style is switchable in the tray (**Notifications** menu) or via `NotificationType` in `config.json`: `overlay` (in-game banner), `windows_toast` (plain system toast, silent), or `none` (sound only).

---

## Settings: `config.json`

Next to `Clippy.exe` you'll find `config.example.json` — a commented sample of every setting. Copy it to `config.json` for manual tuning.

| Parameter | Default | Description |
|---|---|---|
| `OutputFolder` | `clips` | Folder for saved clips |
| `LogsFolder` | `logs` | Folder for debug logs (last 10 sessions kept) |
| `ShortClipSeconds` | `30` | Short clip length (F8) |
| `LongClipSeconds` | `180` | Long clip length (F9) |
| `ShortClipHotkey` | `F8` | Short clip hotkey (F1–F12, NumPad 0–9) |
| `LongClipHotkey` | `F9` | Long clip hotkey (F1–F12, NumPad 0–9) |
| `PlaySoundNotification` | `true` | Play a beep in your headphones on save |
| `CustomSoundPath` | `""` | Custom notification sound (`.wav`); empty or missing file = the sound built into `Clippy.exe` |
| `NotificationType` | `overlay` | Save notification style: `overlay` (Medal / ShadowPlay banner), `windows_toast` (silent system toast) or `none` (sound only) |
| `StartMinimizedToTray` | `true` | Start straight in the system tray, no console window |
| `CheckForUpdates` | `true` | Check GitHub for updates on startup |
| `VideoEncoder` | `nvenc_direct` | Encoder: `nvenc_direct` (0-copy VRAM) / `h264_nvenc` / `libx264` |
| `VideoCodec` | `hevc` | Video codec: `hevc` (H.265, ~2× more efficient) / `h264` |
| `VideoBitrateMbps` | `6` | Target video bitrate in Mbps (2–50) |
| `MaxClipsFolderSizeGB` | `0` | Disk quota for clips in GB. `0` = unlimited (10 / 20 / 30 / 50) |
| `AudioEncoder` | `media_foundation` | Audio codec: `media_foundation` (Windows AAC) / `ffmpeg` |

### Disk-space guard

The clips folder is protected from filling your drive: **Clips Storage Limit** in the tray or `MaxClipsFolderSizeGB` in the config.

After every saved clip the folder size is checked: once the total exceeds the quota, the oldest clips are deleted FIFO. Only `*.mp4` files are ever touched — stray files are never removed. By default the quota is off (`0`).

Clips are saved **strictly** on hotkey press. On exit (Exit in the tray menu) Clippy saves nothing extra and releases its resources immediately.

### Memory architecture and bitrate

Clippy continuously holds the last 190 seconds of history in a two-tier store:
1. **RAM head (Tier 1):** only the last 32 seconds of already-encoded video stream live in memory.
2. **Disk cache (Tier 2):** frames older than 32 seconds spill into temporary segments under `%TEMP%\Clippy\spool`. The folder is wiped on exit.

| `VideoBitrateMbps` | RAM head (32 s) usage |
|---|---|
| 12 | ~40 MB |
| 8 | ~27 MB |
| 6 (default) | ~20 MB |
| 4 | ~13 MB |

The short clip (F8, 30 s) is cut **100% from RAM in 0.001 seconds**. The long one (F9, 180 s) is stitched quickly from RAM plus the SSD cache. Total memory use stays pinned at ~50–120 MB.

### HEVC (H.265) codec efficiency

The `VideoCodec` parameter in the config (and the **Video Codec** tray menu) switches between `hevc` (H.265, default) and classic `h264`.

Measured on a real 1080p@53fps recording:

| Codec & bitrate | Short clip (F8, 30 s) | Long clip (F9, 180 s) |
|---|---|---|
| H.264, 6 Mbps | 20.4 MB | 120.3 MB |
| HEVC, 6 Mbps | 18.4 MB | 109.3 MB |
| **HEVC, 3 Mbps** | **10.9 MB** | **60.9 MB** |

With HEVC a three-minute clip in excellent quality takes only **~60 MB** while keeping the picture crystal clear in fast scenes.

---

## System tray menu

Right-click the tray icon next to the clock:

| Menu item | What it does |
|---|---|
| **Open clips folder** | open the clips folder in your default file manager |
| **Open logs folder** | open the folder with debug logs |
| **Open Last Clip** | play the freshest clip in your default player |
| **Open config.json** | open the settings file in a text editor |
| **Short Clip Duration** | short clip length: 15 / 30 / 60 seconds |
| **Long Clip Duration** | long clip length: 1 / 2 / 3 / 5 minutes |
| **Video Bitrate / RAM** | bitrate: 2 to 50 Mbps |
| **Video Encoder** | encoder: `nvenc_direct` (0-copy VRAM), `h264_nvenc` or `libx264` |
| **Video Codec** | codec: `HEVC (H.265)` or `H.264` |
| **Short Clip Hotkey** | independent hotkey (F1–F12, NumPad 0–9) |
| **Long Clip Hotkey** | independent hotkey (F1–F12, NumPad 0–9) |
| **Select Clips Folder...** | pick the clips folder via the system dialog |
| **Clips Storage Limit** | disk quota: Unlimited / 10 / 20 / 30 / 50 GB |
| **Check for Updates** | check GitHub for a new version and update in one click |
| **Restart** | soft-restart Clippy |
| **Exit** | quit safely |

Settings apply on the fly and `config.json` is rewritten on disk automatically.

---

## System requirements

- **OS:** Windows 10 (build 19041+) or Windows 11, x64.
- **Runtime:** none — the binary is pure Native AOT, no .NET install needed.
- **GPU:** NVIDIA GeForce (GTX 900+ / GTX 10xx / RTX) for hardware Direct NVENC (0-copy VRAM). On AMD/Intel graphics a software x264 mode is available when an external ffmpeg is present.
- **Bridge library:** `clippy_nvenc.dll` ships in the release and must sit next to `Clippy.exe`.
- **Audio:** no microphone needed — recording captures the system output device (exactly what you hear in your headphones or speakers).

---

## Command-line keys

Day to day you just click the shortcut. For automation and diagnostics:

| Flag | Purpose |
|---|---|
| `--console`, `--debug` | launch with a console window for the live log |
| `--help`, `-h`, `--usage` | print help |
| `--record [seconds]` | record video to a file for a fixed duration |
| `--audio` | enable system audio for `--record` |
| `--hotkeys` | enable hotkey handling for `--record` |
| `--tray`, `--minimized` | force start in the system tray |
| `--encoder=NAME` | override the video encoder for this run |
| `--fps-mode=MODE` | frame pacing: `passthrough` (default) / `vfr` / `cfr` |
| `--export-at=S` `--export-dur=S` | automatically cut a clip during a test recording |
| `--smoke` | end-to-end subsystem integrity check |
| `--test-parsers`, `--test-ring`, `--test-mp4`, `--test-audio-timeline` | built-in self-tests for containers and the buffer |
| `--test-nvenc-native` | 30-frame hardware test through direct NVENC in VRAM |

---

## Building from source

You need the .NET 10 SDK and Visual Studio 2022 Build Tools with the C++ components (for the native bridge and the Native AOT link step):

```powershell
# 1. Compile the C Direct-NVENC bridge
src\native\build.cmd

# 2. Publish the native binary
dotnet publish src/Clippy.csproj -c Release -r win-x64 -p:PublishAot=true -o publish
```

The finished self-contained build lands in `publish/`. See [CONTRIBUTING.md](CONTRIBUTING.md) for the full workflow.

---

## License

Distributed under the permissive [MIT](LICENSE) license.
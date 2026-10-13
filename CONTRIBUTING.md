# Contributing to Clippy

Thanks for your interest in improving Clippy! This document covers building the project
locally and running its self-tests before you send a change.

## Requirements

- **.NET 10 SDK**
- **Visual Studio 2022 Build Tools** with the **Desktop development with C++** components
  (needed to compile the native NVENC bridge and to link the Native AOT binary)
- Windows 10 (build 19041+) or Windows 11, x64

## Build

```powershell
# 1. Compile the C Direct-NVENC bridge (produces clippy_nvenc.dll)
src\native\build.cmd

# 2. Publish the native AOT binary
dotnet publish src/Clippy.csproj -c Release -r win-x64 -p:PublishAot=true -o publish
```

The self-contained build lands in `publish/`. The bridge DLL must exist **before** publish —
the project copies it into the output folder as part of the build.

For day-to-day development a plain `dotnet build` is enough and much faster than AOT.

## Self-tests

Run these from the publish (or build) output directory; every one must exit with code 0:

```powershell
.\Clippy.exe --smoke
.\Clippy.exe --test-parsers
.\Clippy.exe --test-ring
.\Clippy.exe --test-mp4
.\Clippy.exe --test-audio-timeline
.\Clippy.exe --test-nvenc-native   # needs an NVIDIA GPU
```

- `--smoke` is an end-to-end check of the whole pipeline — run it for any change.
- `--test-mp4` shells out to `ffprobe`, so make sure `ffmpeg`/`ffprobe` are on your `PATH`.
- `--test-nvenc-native` requires an NVIDIA GPU; skip it only if you genuinely cannot run it.

## Guidelines

- Keep changes focused: one fix or feature per pull request.
- Match the existing code style and comment where the *why* is non-obvious.
- Describe what you tested in the pull request (which self-tests, with what result).

## Reporting bugs

Use the [bug report form](.github/ISSUE_TEMPLATE/bug_report.yml) and attach the files from
the `logs/` folder — they are usually enough to pinpoint the problem.

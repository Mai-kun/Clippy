@echo off
REM Builds clippy_nvenc.dll with MSVC.
REM
REM Two non-obvious things are handled here, both found the hard way:
REM
REM  1. vcvarsall.bat locates vswhere through PATH, and the Visual Studio Installer folder is not on
REM     PATH by default. Without the line below, vcvars aborts with "vswhere.exe is not recognized" and
REM     the message points at PATH rather than at the missing PATH entry, so it reads as a broken
REM     installation. vcvars writes to stdout, which is discarded, but the failure goes to stderr and
REM     stays visible -- so the one useful clue arrives exactly when everything else has been silenced.
REM
REM  2. The install path is resolved by find-vs.ps1 instead of inline. A for/f over vswhere is a
REM     quoting trap, because that path sits under Program Files (x86) and cmd does not survive the
REM     parentheses in an unexpanded environment reference.
setlocal

set "VSPATH="
for /f "usebackq delims=" %%i in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0find-vs.ps1"`) do set "VSPATH=%%i"
if "%VSPATH%"=="" goto :novs

set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
call "%VSPATH%\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul
if errorlevel 1 goto :novcvars

echo [build] toolchain: %VSPATH%
pushd "%~dp0"

REM /O2 is the only optimisation worth having: the time goes into NVENC, not into this file.
REM /LD builds a DLL; /D_USRDLL and /D_WINDLL match the CRT and export semantics the C# side expects.
REM d3d11.lib supplies the ID3D11Texture2D vtable used by GetDesc.
cl.exe /nologo /O2 /LD /D_USRDLL /D_WINDLL /I. clippy_nvenc.c /link /OUT:clippy_nvenc.dll d3d11.lib
if errorlevel 1 goto :compilefail

echo [build] ok
dir /b clippy_nvenc.* 2>nul
popd
endlocal
exit /b 0

:novs
echo [build] no Visual Studio with the C++ toolchain was found
endlocal
exit /b 1

:novcvars
echo [build] vcvarsall failed
endlocal
exit /b 1

:compilefail
echo [build] FAILED
popd
endlocal
exit /b 1
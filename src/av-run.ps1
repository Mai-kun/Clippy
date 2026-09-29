# Drives the A/V sync test: a full-screen white flash and a beep on one shared grid, then the
# recording itself.
#
# The visual marker used to be `ffmpeg -f fullscreen`, and that stopped working silently: ffmpeg 9 on
# this box has no fullscreen and no gdi muxer, so the command failed, the window never appeared, and
# every clip came out black while the audio beeps played fine. measure_sync then reported 0 flashes
# and refused to measure. A marker that cannot be shown is worse than none, so it is now a plain
# topmost white form -- always available, and started at the same instant as the beep it pairs with.
#
# Flash and beep are driven from ONE loop on purpose. Sleep-then-play in two places drifts, and the
# whole measurement is a 20 ms quantity; a separate timer would put its own error into the answer.
param(
    [string]$Exe = "$env:TEMP\aotchk\Clippy.exe",
    [int]$Seconds = 40
)

$ErrorActionPreference = 'Continue'
$wav = "$env:TEMP\av.wav"
$clips = Join-Path (Split-Path $Exe) 'clips'
$log = "$env:TEMP\avtest.log"

"=== A/V test $(Get-Date -Format o) ===" | Set-Content $log
Get-Process ffmpeg -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Job | Remove-Job -Force -ErrorAction SilentlyContinue
Start-Sleep 1

# Markers run in THIS process, not in a job. A Start-Job child has no interactive window station:
# the beep came through fine, but the white form never painted, which is a silent failure -- the
# recording looked healthy and the clip was black. The recording is backgrounded instead, so this
# process is free to own the marker.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.TopMost = $true
$form.StartPosition = 'Manual'
# Primary monitor only: Clippy captures the primary display, and on this two-monitor box a window
# on the secondary would be captured as pure black.
$form.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$form.BackColor = [System.Drawing.Color]::White
$form.Show()
$form.Hide()

$recLog = "$env:TEMP\avrun.log"
$rec = Start-Process -FilePath $Exe -ArgumentList '--record', $Seconds, '--audio' -PassThru -NoNewWindow
Start-Sleep 2
"recording $Seconds s, pid=$($rec.Id)" | Add-Content $log

$deadline = (Get-Date).AddSeconds($Seconds + 4)
$markers = 0
while ((Get-Date) -lt $deadline) {
    # Show, then play. The order is the whole point: the flash must appear at the instant the beep
    # starts, and PlaySync blocks for the wav, so hiding afterwards keeps the two locked together.
    $form.Show()
    $form.Refresh()
    $p = New-Object Media.SoundPlayer $wav
    $p.PlaySync()
    $form.Hide()
    $markers++
    Start-Sleep -Milliseconds 2200
}
"markers played: $markers" | Add-Content $log

$rec.WaitForExit(30000) | Out-Null
"recording finished, exit=$($rec.ExitCode)" | Add-Content $log
Get-Content $recLog -ErrorAction SilentlyContinue | Add-Content $log

$form.Dispose()
Get-Process ffmpeg -ErrorAction SilentlyContinue | Stop-Process -Force

Get-ChildItem $clips -Filter '*.mp4' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 3 Name, Length, LastWriteTime | Out-String | Add-Content $log
"=== done ===" | Add-Content $log


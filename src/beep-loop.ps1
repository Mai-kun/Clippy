# Audio marker for the A/V test. Must be its own PROCESS: a PowerShell background job dies with the
# shell that created it, and the loopback then receives nothing (measured: 0 buffers).
#   powershell -File beep-loop.ps1 -Count 15 -GapMs 4700
param([int]$Count = 15, [int]$GapMs = 4700)

$wav = "$env:TEMP\av.wav"
for ($i = 0; $i -lt $Count; $i++) {
    $player = New-Object Media.SoundPlayer $wav
    $player.PlaySync()
    Start-Sleep -Milliseconds $GapMs
}

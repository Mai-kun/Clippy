# Drives the 60 s A/V sync test: fullscreen video for the visual marker, a looped wav for the audio
# marker, then the recording itself. Kept separate because the fullscreen player did not send audio
# to the loopback endpoint (measured: 0 buffers), so the two markers need two players.
param([int]$Seconds = 60)

$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot
$exe = ".\bin\Release\net10.0-windows10.0.26100.0\win-x64\Clippy.exe"
$av = "$env:TEMP\av.mp4"
$wav = "$env:TEMP\av.wav"

Get-Process ffmpeg -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1

# Visual marker only: a fullscreen player is a reliable presenter, but it fed nothing to loopback.
$video = Start-Process -FilePath ffmpeg -ArgumentList '-hide_banner', '-loglevel', 'error',
    '-stream_loop', '-1', '-i', $av, '-an', '-f', 'fullscreen' -PassThru
Start-Sleep 2

# Audio marker: System.Media.SoundPlayer is confirmed to reach the loopback endpoint.
$beeper = Start-Job -ScriptBlock {
    for ($i = 0; $i -lt 40; $i++) {
        $p = New-Object Media.SoundPlayer "$env:TEMP\av.wav"
        $p.PlaySync()
        Start-Sleep -Milliseconds 4920
    }
}
Start-Sleep 2

Write-Output "video pid=$($video.Id), recording $Seconds s"
& $exe --record $Seconds --audio
Write-Output 'recording finished'

Stop-Process -Id $video.Id -Force -ErrorAction SilentlyContinue
Remove-Job $beeper -Force -ErrorAction SilentlyContinue
Get-Process ffmpeg -ErrorAction SilentlyContinue | Stop-Process -Force

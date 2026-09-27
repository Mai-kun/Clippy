# Runs N capture-only bisection runs and reports, for each, how many video writes were
# STARTED vs COMPLETED. A gap means a write is stuck inside stdin.Write.
param([int]$Runs = 10, [int]$Seconds = 6)

$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot
$exe = ".\bin\Release\net10.0-windows10.0.26100.0\win-x64\Clippy.exe"
$out = ".\output"
$beep = "$env:TEMP\beep.wav"

if (-not (Test-Path $beep)) {
    ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=1000:duration=0.3" -ac 2 -ar 48000 $beep
}

$results = @()
for ($i = 1; $i -le $Runs; $i++) {
    $before = @(Get-ChildItem "$out\events-*.csv" -ErrorAction SilentlyContinue).Count
    $log = ".\trace$i.log"
    Start-Process -FilePath $exe -ArgumentList '--record', $Seconds, '--audio-capture-only' `
        -RedirectStandardOutput $log -NoNewWindow | Out-Null
    Start-Sleep -Seconds 2
    (New-Object Media.SoundPlayer $beep).Play()
    Start-Sleep -Seconds ($Seconds + 8)

    $eventFile = @(Get-ChildItem "$out\events-*.csv" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1)
    $line = Get-Content $log | Select-String 'stopped after'
    $frames = if ($line) { [int](($line.Line -replace '\D', '')) } else { -1 }
    $ev = $eventFile[0].FullName

    $started = 0; $done = 0; $lastStdinBefore = 0
    if ($ev -and (Test-Path $ev)) {
        $lines = Get-Content $ev
        $started = @($lines | Select-String ',STDIN_BEFORE,').Count
        $done = @($lines | Select-String ',STDIN_AFTER,').Count
    }
    $stalled = ($started -gt 0) -and ($started - $done -gt 0)
    $results += [pscustomobject]@{
        Run = $i; Frames = $frames; Started = $started; Completed = $done
        Stuck = $started - $done; Verdict = if ($stalled) { 'STALL' } else { 'ok' }
        Events = if ($ev) { Split-Path $ev -Leaf } else { '' }
    }
    $results[-1] | Format-Table -AutoSize | Out-String -Width 120 | Write-Output
}

Write-Output '=== SUMMARY ==='
$results | Format-Table -AutoSize | Out-String -Width 160 | Write-Output
$stalls = @($results | Where-Object { $_.Verdict -eq 'STALL' })
Write-Output "stalls: $($stalls.Count) of $($results.Count)"
$stalls | ForEach-Object { Write-Output "  run $($_.Run) -> $($_.Events)" }

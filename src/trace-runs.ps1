# Runs N capture-only bisection runs and reports, for each, how many video writes were
# STARTED vs COMPLETED. A gap means a write is stuck inside stdin.Write.
param([int]$Runs = 10, [int]$Seconds = 5, [switch]$ArchiveEvery5, [switch]$Defender, [ValidateSet('static','active')][string]$Mode = 'static')

# Measures two things per run:
#  1) startup latency: wall-clock from Start-Process until the first FB_ENTER lands in the event log.
#     If this grows run over run, the cost is in process start / I-O, not in capture.
#  2) with -ArchiveEvery5, the output folder is moved aside every 5 runs, so the run never sees an
#     accumulating directory. If that removes the degradation, the volume of files is the cause.
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot
$exe = ".\bin\Release\net10.0-windows10.0.26100.0\win-x64\Clippy.exe"
$out = ".\output"
$beep = "$env:TEMP\beep.wav"

if ($Defender) {
    foreach ($p in @($PSScriptRoot, $out)) {
        try {
            Add-MpPreference -ExclusionPath $p -ErrorAction Stop
            Write-Output "defender exclusion added: $p"
        } catch {
            Write-Output "defender exclusion FAILED for ${p}: $($_.Exception.Message)"
        }
    }
}

if (-not (Test-Path $beep)) {
    ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=1000:duration=0.3" -ac 2 -ar 48000 $beep
}

# For -Mode active: a fullscreen, never-ending video, so the captured content changes every frame.
# WGC only emits frames when something actually changes, so a static desktop legitimately yields far
# fewer frames than a playing video. That difference is expected, not a defect.
$movie = "$env:TEMP\clippy-active.mp4"
$player = $null
if ($Mode -eq 'active') {
    if (-not (Test-Path $movie)) {
        ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=s=1920x1080:r=60:d=20" `
            -c:v libx264 -preset ultrafast -pix_fmt yuv420p $movie
    }
    $player = Start-Process -FilePath 'ffmpeg' `
        -ArgumentList '-hide_banner', '-loglevel', 'error', '-re', '-stream_loop', '-1', '-i', $movie,
                      '-f', 'fullscreen' -PassThru
    Start-Sleep 2
    Write-Output "fullscreen video playing (pid $($player.Id))"
}

$rows = @()
for ($i = 1; $i -le $Runs; $i++) {
    if ($ArchiveEvery5 -and ($i % 5 -eq 1) -and $i -gt 1) {
        $stamp = Get-Date -Format 'HHmmss'
        Move-Item $out "$out-archive-$stamp" -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $out | Out-Null
    }

    $seen = @(Get-ChildItem "$out\events-*.csv" -ErrorAction SilentlyContinue).Count
    $t0 = Get-Date
    Start-Process -FilePath $exe -ArgumentList '--record', $Seconds, '--audio-capture-only' `
        -RedirectStandardOutput ".\s$i.log" -NoNewWindow | Out-Null

    # Poll for the first FB_ENTER: that is the first real captured frame.
    $latency = $null
    for ($w = 0; $w -lt 200; $w++) {
        $files = @(Get-ChildItem "$out\events-*.csv" -ErrorAction SilentlyContinue)
        if ($files.Count -gt $seen) {
            $new = $files | Sort-Object LastWriteTime | Select-Object -Last 1
            $hit = Select-String -Path $new.FullName -Pattern ',FB_ENTER,' -SimpleMatch -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($hit) { $latency = ((Get-Date) - $t0).TotalMilliseconds; break }
        }
        Start-Sleep -Milliseconds 50
    }

    Start-Sleep 3
    (New-Object Media.SoundPlayer $beep).Play()
    Start-Sleep -Seconds ($Seconds + 6)

    $line = Get-Content ".\s$i.log" -ErrorAction SilentlyContinue | Select-String 'stopped after'
    $frames = if ($line) { [int](($line.Line -replace '\D', '')) } else { -1 }
    $ev = @(Get-ChildItem "$out\events-*.csv" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1)
    $started = 0; $done = 0
    if ($ev.Count) {
        $lines = Get-Content $ev[0].FullName
        $started = @($lines | Select-String ',STDIN_BEFORE,' -SimpleMatch).Count
        $done = @($lines | Select-String ',STDIN_AFTER,' -SimpleMatch).Count
    }

    $rows += [pscustomobject]@{
        Run = $i; Frames = $frames; Stuck = $started - $done
        StartupMs = if ($null -ne $latency) { [int]$latency } else { -1 }
    }
    Write-Output ("run {0,2}: frames={1,4} stuck={2} startupMs={3}" -f $i, $frames, ($started - $done), $rows[-1].StartupMs)
}

Write-Output '=== SUMMARY ==='
$rows | Format-Table -AutoSize | Out-String -Width 100 | Write-Output
$ok = @($rows | Where-Object { $_.StartupMs -gt 0 })
if ($ok.Count) {
    $first = ($ok | Select-Object -First 5 | Measure-Object StartupMs -Average).Average
    $last = ($ok | Select-Object -Last 5 | Measure-Object StartupMs -Average).Average
    Write-Output ("startup first5 avg = {0:F0} ms, last5 avg = {1:F0} ms, delta = {2:F0} ms" -f $first, $last, ($last - $first))
}
$good = @($rows | Where-Object { $_.Frames -gt 100 }).Count
Write-Output "runs with >100 frames: $good / $($rows.Count);  total stalls: $((($rows | Measure-Object Stuck -Sum).Sum))"

if ($player) {
    Stop-Process -Id $player.Id -Force -ErrorAction SilentlyContinue
    Write-Output "fullscreen video stopped"
}


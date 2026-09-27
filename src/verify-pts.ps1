# Verifies that each output frame's PTS matches its real capture Stopwatch timestamp (<1 ms),
# instead of only checking that the file duration looks plausible.
$ErrorActionPreference = 'Stop'

$video = $args[0]
$csv = [IO.Path]::ChangeExtension($video, '.timing.csv')
if (-not (Test-Path $csv)) { throw "timing log not found: $csv" }

$captured = Import-Csv $csv
Write-Host "captured frames (Stopwatch ground truth): $($captured.Count)"

$pts = & ffprobe -v error -select_streams v:0 -show_entries frame=pts_time `
    -of csv=p=0 $video | ForEach-Object { [double]$_ }
Write-Host "encoded frames in file (ffprobe):        $($pts.Count)"

if ($captured.Count -ne $pts.Count) {
    Write-Host "COUNT MISMATCH: $($captured.Count) captured vs $($pts.Count) encoded -> frames lost in the pipe"
}

# ffmpeg's first PTS is its own start, so both series are normalised to their first frame.
$sw0 = [double]$captured[0].swCaptureMs
$pts0 = $pts[0]

$rows = for ($i = 0; $i -lt [Math]::Min($captured.Count, $pts.Count); $i++) {
    $capture = [double]$captured[$i].swCaptureMs
    $written = [double]$captured[$i].swWriteDoneMs
    [pscustomobject]@{
        Frame        = $i + 1
        PtsS         = $pts[$i]
        CaptureDelta = [Math]::Round(($pts[$i] - ($capture - $sw0) / 1000) * 1000, 2)
        WrittenDelta = [Math]::Round(($pts[$i] - ($written - $sw0) / 1000) * 1000, 2)
    }
}

Write-Host "`nfirst 12 frames (DeltaMs = PTS minus that clock, normalised to frame 1):"
$rows | Select-Object -First 12 | Format-Table -AutoSize | Out-String -Width 100

Write-Host "worst 10 by capture-clock delta:"
$rows | Sort-Object { [Math]::Abs($_.CaptureDelta) } -Descending |
    Select-Object -First 10 | Format-Table -AutoSize | Out-String -Width 100

$maxCapture = ($rows | ForEach-Object { [Math]::Abs($_.CaptureDelta) } | Measure-Object -Maximum).Maximum
$maxWritten = ($rows | ForEach-Object { [Math]::Abs($_.WrittenDelta) } | Measure-Object -Maximum).Maximum
$avgWritten = ($rows | ForEach-Object { [Math]::Abs($_.WrittenDelta) } | Measure-Object -Average).Average

Write-Host "max |PTS - capture instant| = $([Math]::Round($maxCapture, 2)) ms"
Write-Host "max |PTS - write-done instant| = $([Math]::Round($maxWritten, 2)) ms  (avg $([Math]::Round($avgWritten, 2)) ms)"
Write-Host "budget = 1 ms"
if ($maxCapture -gt 1) { Write-Host "FAIL: capture instants do not match PTS within budget" }
else { Write-Host "PASS: every frame within 1 ms" }

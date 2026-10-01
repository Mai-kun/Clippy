# Resource sampler for a live Clippy run.
#
# Two things this had to be taught the hard way:
#   1. Rows are flushed as they are produced. The first version buffered everything and wrote at
#      the end, so whatever went wrong at shutdown took the whole run's data with it.
#   2. No Get-Counter. '\GPU Engine(*)\Utilization Percentage' expands to hundreds of instances
#      and takes seconds per call, which made the loop sail past its own deadline. nvidia-smi
#      answers the question that actually matters here -- is NVENC busy -- in milliseconds.
#
# Not part of the product: a one-off harness for a manual test run.
#
# The parameter is -ProcId, not -Pid: $PID is a read-only automatic variable in PowerShell, and a
# parameter by that name can never be assigned, so -File ... -Pid 20972 fails before the first line
# of the body runs. That is exactly how the first two versions of this script died silently.
param([int]$ProcId, [int]$Seconds, [string]$Out)

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$w = New-Object System.IO.StreamWriter($Out, $false, (New-Object System.Text.UTF8Encoding($false)))
$w.AutoFlush = $true
$w.WriteLine('t,ws_mb,private_mb,handles,threads,cpu_s,cpu_pct,read_mb,write_mb,enc_pct,gpu_mem_mb')

$prevCpu = 0.0
$prevT = 0.0
$cores = [Environment]::ProcessorCount

while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
  $t = [math]::Round($sw.Elapsed.TotalSeconds, 2)
  $pr = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
  if (-not $pr) { $w.WriteLine("$t,DEAD"); break }

  $cpu = [double]$pr.CPU
  # CPU% over this interval, not since start: what causes a stutter is the instantaneous cost.
  # The first row is skipped, because dt there is the few milliseconds since the loop began and
  # dividing accumulated CPU by it invents a few-hundred-percent spike that never happened.
  if ($prevT -eq 0.0) { $prevCpu = $cpu; $prevT = $t; $cpuPct = -1 }
  $dt = $t - $prevT
  if ($dt -gt 0) { $cpuPct = [math]::Round((($cpu - $prevCpu) / $dt) * 100 / $cores, 2) }
  $prevCpu = $cpu; $prevT = $t

  # Get-Process does not expose the IO counters, so they come from Win32_Process. A failure here
  # must not take the sample down with it, hence the silent default.
  $io = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcId" -ErrorAction SilentlyContinue
  $rd = if ($io) { [math]::Round([uint64]$io.ReadTransferCount / 1MB, 1) } else { 0 }
  $wr = if ($io) { [math]::Round([uint64]$io.WriteTransferCount / 1MB, 1) } else { 0 }

  $g = (& nvidia-smi --query-gpu=utilization.encoder,memory.used --format=csv,noheader,nounits 2>$null | Out-String).Trim()
  $enc = 0; $gmem = 0
  if ($g -match '(\d+)\s*,\s*(\d+)') { $enc = [int]$matches[1]; $gmem = [int]$matches[2] }

  $w.WriteLine(('{0},{1:N1},{2:N1},{3},{4},{5:N2},{6},{7},{8},{9},{10}' -f `
    $t, ($pr.WorkingSet64/1MB), ($pr.PrivateMemorySize64/1MB), $pr.HandleCount, $pr.Threads.Count,
    $cpu, $cpuPct, $rd, $wr, $enc, $gmem))

  Start-Sleep -Milliseconds 500
}
$w.Close()
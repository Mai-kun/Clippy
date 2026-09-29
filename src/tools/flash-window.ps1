# Visual marker for the A/V test: a normal maximised WinForms window (NOT exclusive fullscreen, which
# WGC did not capture here) whose background alternates black/white every 5 s.
#   powershell -File flash-window.ps1 [-PeriodMs 5000] [-OnMs 100]
param([int]$PeriodMs = 5000, [int]$OnMs = 250)
# OnMs must stay BELOW the 0.5 s marker-merge threshold in av-sync.py. A longer pulse makes one
# flash register as two hits and the pairing then binds each half to a different beep. 250 ms is
# several times the ~67 ms frame interval at 15 fps, so it is reliably captured.
#
# ALWAYS verify a generated test media file with `ffprobe -show_entries format=duration` immediately
# after creating it, before anything consumes it. This project has twice lost rounds to unverified
# files with an unexpected duration.

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'clippy av marker'
$form.FormBorderStyle = 'FixedSingle'   # a real window, not borderless fullscreen
$form.StartPosition = 'Manual'
$form.Location = [System.Drawing.Point]::new(0, 0)
$form.Size = [System.Drawing.Size]::new(1920, 1080)
$form.TopMost = $true
$form.BackColor = [System.Drawing.Color]::Black

$panel = New-Object System.Windows.Forms.Panel
$panel.Dock = 'Fill'
$panel.BackColor = [System.Drawing.Color]::Black
$form.Controls.Add($panel)

# A block that sweeps across continuously. Without it the screen is almost entirely black, and WGC
# only emits a frame when something actually changes -- a static window produced 3 frames in 8 s.
$block = New-Object System.Windows.Forms.Panel
$block.Size = [System.Drawing.Size]::new(260, 260)
$block.BackColor = [System.Drawing.Color]::Red
$panel.Controls.Add($block)

$sweep = New-Object System.Windows.Forms.Timer
$sweep.Interval = 30
$script:x = 0
$sweep.Add_Tick({
    $script:x = ($script:x + 24) % 1920
    $block.Location = [System.Drawing.Point]::new($script:x, 300)
})

# A true short pulse, not a square wave: a fast timer turns the panel white only for the first
# OnMs of each period and black afterwards. The earlier toggle-per-period version left the screen
# white for a whole period, so the detector split one flash into several and matching was impossible.
$clock = New-Object System.Windows.Forms.Timer
$clock.Interval = 100
$elapsed = @{ ms = 0 }
$clock.Add_Tick({
    $script:elapsed.ms = $script:elapsed.ms + $clock.Interval
    if ($script:elapsed.ms -ge $PeriodMs) {
        $script:elapsed.ms = 0
    }
    # The middle case must set BLACK explicitly. Without it the panel kept the last colour, so a
    # 300 ms "pulse" stayed white for the whole 5 s period and the detector split it into many hits.
    if ($script:elapsed.ms -le $OnMs) {
        $panel.BackColor = [System.Drawing.Color]::White
    } else {
        $panel.BackColor = [System.Drawing.Color]::Black
    }
    $panel.Refresh()
})

$form.Add_Shown({
    $form.WindowState = 'Maximized'
    $sweep.Start()
    # Start BLACK and let the first tick turn it white: otherwise the window is white for the first
    # period after launch and the marker timeline has a bogus origin.
    $panel.BackColor = [System.Drawing.Color]::Black
    $panel.Refresh()
    $elapsed.ms = 0
    $clock.Start()
})
[void]$form.ShowDialog()



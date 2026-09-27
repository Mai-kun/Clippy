# Visual marker for the A/V test: a normal maximised WinForms window (NOT exclusive fullscreen, which
# WGC did not capture here) whose background alternates black/white every 5 s.
#   powershell -File flash-window.ps1 [-PeriodMs 5000] [-OnMs 100]
param([int]$PeriodMs = 5000, [int]$OnMs = 100)

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

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = $PeriodMs
$state = @{ on = $false }

# Flash white briefly on each tick, then black for the rest of the period: a short bright pulse is
# easy to detect by mean luma, and unambiguous against the black background.
$timer.Add_Tick({
    if ($script:state.on) {
        $panel.BackColor = [System.Drawing.Color]::Black
        $script:state.on = $false
    } else {
        $panel.BackColor = [System.Drawing.Color]::White
        $script:state.on = $true
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
    $timer.Start()
})
[void]$form.ShowDialog()

<#
.SYNOPSIS
    Live window tests for Claude Status: worker focusing (terminals, multi-window apps) and focus stealing.
    Started hidden by run-tests.ps1 -Live - the same way start-indicator.vbs starts the indicator - because
    the indicator only reads other processes' console windows when its own console is hidden.
#>
param([string]$ResultFile, [string]$Tmp)
$tmp = $Tmp
$script:ResultFile = $ResultFile
. (Join-Path $PSScriptRoot 'common.ps1')
$WW = [ClaudeStatus.WorkerWindows]
$why = ''
$me = StartOf $PID
$spawned = @()

function Wait-Child([System.Diagnostics.Process]$parent, [string]$name) {
    for ($i = 0; $i -lt 50; $i++) {
        Start-Sleep -Milliseconds 200
        $c = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($parent.Id) AND Name='$name'" | Select-Object -First 1
        if ($c) { return [int]$c.ProcessId }
    }
    throw "no $name child of $($parent.Id)"
}

# A fake "claude" (a sleeping powershell) inside a cmd console with a unique title. Uses the PC's
# default terminal (Windows Terminal on Windows 11), or a classic console window with -Classic.
function Start-FakeTerminal([string]$marker, [switch]$Classic) {
    $line = "title $marker & `"$winPs`" -NoProfile -Command Start-Sleep 150"
    if ($Classic) {
        $outer = Start-Process conhost.exe -ArgumentList "cmd.exe /c $line" -PassThru
        $cmdPid = Wait-Child $outer 'cmd.exe'
        $cmd = Get-Process -Id $cmdPid
    } else { $cmd = Start-Process cmd.exe -ArgumentList '/c', $line -PassThru }
    $child = Wait-Child $cmd 'powershell.exe'
    $script:spawned += $cmd
    return [pscustomobject]@{ Marker = $marker; Cmd = $cmd; Pid = $child; Start = (StartOf $child); Window = [IntPtr]::Zero }
}
function New-Marker([string]$p) { "CS-TEST-$p-" + [Guid]::NewGuid().ToString('N').Substring(0, 6) }

try {
    Section 'Worker focusing: terminal workers'
    # Pairs launched back to back: this is when Windows Terminal sometimes gives the pseudo-console window to
    # its OpenConsole.exe instead of the shell, so the window can't be found through the process tree alone.
    $terms = @()
    foreach ($n in 1..3) { $terms += Start-FakeTerminal (New-Marker "A$n"); $terms += Start-FakeTerminal (New-Marker "B$n") }
    $classic = Start-FakeTerminal (New-Marker 'CLASSIC') -Classic
    $terms += $classic
    Start-Sleep -Seconds 2   # let the terminals apply their titles
    foreach ($tm in $terms) { $tm.Window = $WW::Resolve($tm.Pid, $tm.Start, $null, [ref]$why) }
    $distinct = @($terms | ForEach-Object { $_.Window.ToInt64() } | Sort-Object -Unique).Count
    $hostNames = ($terms | Where-Object { $_.Window -ne [IntPtr]::Zero } | ForEach-Object { (Get-Process -Id ([TNative]::WindowPid($_.Window))).ProcessName } | Sort-Object -Unique) -join ', '
    Info "$($terms.Count) terminal workers -> $distinct distinct windows (hosts: $hostNames)"
    foreach ($tm in $terms) {
        $title = if ($tm.Window -ne [IntPtr]::Zero) { [TNative]::Title($tm.Window) } else { '' }
        if ($distinct -eq $terms.Count) {
            Check "terminal worker $($tm.Marker.Substring(8, 2)) resolves to exactly its own window" ($title -like "*$($tm.Marker)*") "got '$title' $why"
        } else {
            # Some consoles opened as tabs of one window: that window shows one of its tabs' titles.
            $mates = $terms | Where-Object { $_.Window -eq $tm.Window } | ForEach-Object { $_.Marker }
            Check "terminal worker $($tm.Marker.Substring(8, 2)) resolves to the window hosting its tab" ($tm.Window -ne [IntPtr]::Zero -and @($mates | Where-Object { $title -like "*$_*" }).Count -gt 0) "got '$title'"
        }
    }
    $a = $terms[0]; $b = $terms[1]
    Check 'click worker A: its window comes to the front' ($WW::Activate($a.Window) -and [TNative]::GetForegroundWindow() -eq $a.Window)
    Start-Sleep -Milliseconds 300
    Check 'click worker B: its window comes to the front' ($WW::Activate($b.Window) -and [TNative]::GetForegroundWindow() -eq $b.Window)
    Start-Sleep -Milliseconds 300
    Check 'click the classic-console worker: its window comes to the front' ($WW::Activate($classic.Window) -and [TNative]::GetForegroundWindow() -eq $classic.Window)

    $closed = $terms[3]
    Stop-Process -Id $closed.Pid -Force; Stop-Process -Id $closed.Cmd.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500
    Check 'closed worker window: refuses (never falls back to another window)' ($WW::Resolve($closed.Pid, $closed.Start, $null, [ref]$why) -eq [IntPtr]::Zero -and $why -match 'no longer running') $why

    Section 'Worker focusing: an app with several windows (like VS Code)'
    $hostScript = Join-Path $tmp 'fakehost.ps1'; $hostOut = Join-Path $tmp 'fakehost.txt'
    @'
param([string]$Out)
Add-Type -AssemblyName System.Windows.Forms
$a = New-Object Windows.Forms.Form -Property @{ Text = 'CS-HOST-A'; Width = 320; Height = 160; StartPosition = 'Manual'; Left = 60; Top = 60 }
$b = New-Object Windows.Forms.Form -Property @{ Text = 'CS-HOST-B'; Width = 320; Height = 160; StartPosition = 'Manual'; Left = 420; Top = 60 }
$a.Show(); $b.Show(); $a.Hide(); $b.Hide(); $a.Show(); $b.Show()   # a hidden-started process hides its first shown window
$ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$c1 = Start-Process $ps -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 120' -PassThru -NoNewWindow
$c2 = Start-Process $ps -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 120' -PassThru -NoNewWindow
"$($c1.Id),$($c2.Id),$($a.Handle),$($b.Handle)" | Set-Content $Out
$t = New-Object Windows.Forms.Timer -Property @{ Interval = 120000 }; $t.add_Tick({ [Windows.Forms.Application]::Exit() }); $t.Start()
[Windows.Forms.Application]::Run()
'@ | Set-Content $hostScript
    $hp = Start-Process $winPs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$hostScript`"", '-Out', "`"$hostOut`"" -PassThru -WindowStyle Hidden
    $script:spawned += $hp
    for ($i = 0; $i -lt 60 -and -not (Test-Path $hostOut); $i++) { Start-Sleep -Milliseconds 250 }
    Start-Sleep -Milliseconds 500
    $parts = (Get-Content $hostOut).Split(','); $c1 = [int]$parts[0]; $c2 = [int]$parts[1]; $hA = [IntPtr][long]$parts[2]; $hB = [IntPtr][long]$parts[3]
    $c1s = StartOf $c1; $c2s = StartOf $c2; $hps = StartOf $hp.Id
    $r = $WW::Resolve($c1, $c1s, $null, [ref]$why)
    Check 'multi-window app, no anchor: refuses instead of guessing' ($r -eq [IntPtr]::Zero -and $why -match '2 windows') $why
    $anA = New-Object ClaudeStatus.Anchor; $anA.Hwnd = $hA.ToInt64(); $anA.Pid = $hp.Id; $anA.Started = $hps
    $anB = New-Object ClaudeStatus.Anchor; $anB.Hwnd = $hB.ToInt64(); $anB.Pid = $hp.Id; $anB.Started = $hps
    Check 'two workers in one app: each resolves to its own anchored window' ($WW::Resolve($c1, $c1s, $anA, [ref]$why) -eq $hA -and $WW::Resolve($c2, $c2s, $anB, [ref]$why) -eq $hB)
    $bad = New-Object ClaudeStatus.Anchor; $bad.Hwnd = $hA.ToInt64(); $bad.Pid = $hp.Id; $bad.Started = $hps.AddSeconds(-30)
    Check 'anchor whose process identity changed (PID reuse) is rejected' ($WW::Resolve($c1, $c1s, $bad, [ref]$why) -eq [IntPtr]::Zero)
    $foreign = New-Object ClaudeStatus.Anchor; $foreign.Hwnd = $a.Window.ToInt64(); $foreign.Pid = [int][TNative]::WindowPid($a.Window); $foreign.Started = StartOf $foreign.Pid
    Check "anchor pointing at another app's window (a terminal) is rejected" ($WW::Resolve($c1, $c1s, $foreign, [ref]$why) -eq [IntPtr]::Zero)
    $null = $WW::Activate($hB); Start-Sleep -Milliseconds 300
    $cap = $WW::CaptureForeground($c1, $c1s)
    Check 'anchor capture: the foreground window of the right app is recorded' ($null -ne $cap -and $cap.Hwnd -eq $hB.ToInt64() -and $cap.Pid -eq $hp.Id)
    $null = $WW::Activate($a.Window); Start-Sleep -Milliseconds 300
    Check 'anchor capture: an unrelated foreground window is never recorded' ($null -eq $WW::CaptureForeground($c1, $c1s))
    Check 'click an anchored worker: its exact window comes to the front' ($WW::Activate($WW::Resolve($c2, $c2s, $anB, [ref]$why)) -and [TNative]::GetForegroundWindow() -eq $hB)

    Section 'Focus stealing and always-on-top (running indicator)'
    $root = New-Root @{ soundOnDone = $false; soundOnNeedsYou = $false; soundOnError = $false; toastOnNeedsYou = $false; toastOnError = $false }
    $live = New-Object ClaudeStatus.IndicatorForm($root, [single]0, $false)
    $live.Show(); [System.Windows.Forms.Application]::DoEvents()
    $null = $WW::Activate($a.Window); Start-Sleep -Milliseconds 300
    $fgBefore = [TNative]::GetForegroundWindow()
    $stolen = $false
    foreach ($st in 'WORKING', 'DONE', 'WORKING', 'NEEDS_YOU', 'WORKING', 'ERROR') {
        Put $root 'live' $st 'Toah_Nipi_Portal' 0 1 $PID $me
        for ($i = 0; $i -lt 8; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
        if ([TNative]::GetForegroundWindow() -ne $fgBefore -or $live.ContainsFocus) { $stolen = $true }
    }
    Check 'state changes (WORKING/DONE/NEEDS YOU/ERROR) are picked up live' ($live.Surfaced.State -eq 'ERROR')
    Check 'state changes never steal keyboard focus' (-not $stolen -and $fgBefore -eq $a.Window)
    $ex = [TNative]::GetWindowLong($live.Handle, -20)
    Check 'Worker Board is a native topmost, non-activating window' (($ex -band 0x8) -ne 0 -and ($ex -band 0x08000000) -ne 0)
    $live.GetType().GetMethod('SetView', [Reflection.BindingFlags]'NonPublic,Instance').Invoke($live, @($false)); [System.Windows.Forms.Application]::DoEvents()
    $ex = [TNative]::GetWindowLong($live.Handle, -20)
    $g = $live.CreateGraphics(); $expectW = [int][Math]::Round(124 * $g.DpiX / 96); $g.Dispose()
    Check 'Compact is a native topmost, non-activating window; switching kept the sessions' (($ex -band 0x8) -ne 0 -and $live.ClientSize.Width -eq $expectW -and $live.Workers.Count -eq 1)
    Check 'switching views did not take focus' ([TNative]::GetForegroundWindow() -eq $fgBefore)
    $live.GetType().GetMethod('BuildMenu', [Reflection.BindingFlags]'NonPublic,Instance').Invoke($live, @())
    $menu = $live.GetType().GetField('menu', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($live)
    ($menu.Items | Where-Object { $_.Text -eq 'View' }).DropDownItems[0].PerformClick(); [System.Windows.Forms.Application]::DoEvents()
    Check 'View > Worker Board in the right-click menu switches back immediately' ($live.IsBoard -and $live.ClientSize.Width -gt $expectW)

    Section 'Minimize to tray (running indicator)'
    $boundsBefore = $live.Bounds
    $live.MinimizeNow(); [System.Windows.Forms.Application]::DoEvents()
    Check 'minimize hides the window' (-not $live.Visible)
    Put $root 'live' 'NEEDS_YOU' 'Toah_Nipi_Portal' 0 1 $PID $me
    Put $root 'live2' 'WORKING' 'Toah_Nipi_Website' 0 2 $PID $me
    for ($i = 0; $i -lt 12; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
    Check 'while minimized, state changes are still picked up (board and tray)' ($live.Surfaced.State -eq 'NEEDS_YOU' -and $live.Workers.Count -eq 2 -and $live.TrayText -like 'Claude Status: NEEDS YOU*' -and -not $live.Visible) $live.TrayText
    Remove-Item (Join-Path $root 'state\sessions\live2.json')
    for ($i = 0; $i -lt 12; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
    $null = $WW::Activate($a.Window); Start-Sleep -Milliseconds 200
    $fgBefore = [TNative]::GetForegroundWindow()
    $handle = $live.Handle
    $live.RestoreNow(); [System.Windows.Forms.Application]::DoEvents()
    Check 'restore shows the same window with the same view, size and position' ($live.Visible -and $live.Handle -eq $handle -and $live.IsBoard -and $live.Bounds -eq $boundsBefore) "$boundsBefore -> $($live.Bounds)"
    Check 'restored window is topmost again and did not steal keyboard focus' ((([TNative]::GetWindowLong($live.Handle, -20)) -band 0x8) -ne 0 -and [TNative]::GetForegroundWindow() -eq $fgBefore)
    $live.RestoreNow(); [System.Windows.Forms.Application]::DoEvents()
    for ($k = 0; $k -lt 5; $k++) { $live.MinimizeNow(); [System.Windows.Forms.Application]::DoEvents(); $live.RestoreNow(); [System.Windows.Forms.Application]::DoEvents() }
    $forms = @([System.Windows.Forms.Application]::OpenForms | Where-Object { $_ -is [ClaudeStatus.IndicatorForm] })
    Check 'repeated minimize/restore (and restore while open) keeps exactly one window' ($forms.Count -eq 1 -and $live.Visible -and $live.Handle -eq $handle)
    $live.MinimizeNow(); [System.Windows.Forms.Application]::DoEvents()
    ($live.TrayMenu.Items | Where-Object { $_.Text -eq 'Quit' }).PerformClick(); [System.Windows.Forms.Application]::DoEvents()
    Check 'tray Quit (while minimized) exits: window closed and disposed' ($live.IsDisposed)
    if (-not $live.IsDisposed) { $live.Close(); $live.Dispose() }
    Skip 'target window on another monitor' 'single-monitor machine; activation does not depend on the monitor'
} catch {
    Check 'live tests ran without a script error' $false ($_.Exception.Message + ' @ ' + $_.InvocationInfo.ScriptLineNumber)
} finally {
    foreach ($p in $spawned) {
        try {
            Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id)" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        } catch {}
    }
}
exit $(if ($script:fail) { 1 } else { 0 })

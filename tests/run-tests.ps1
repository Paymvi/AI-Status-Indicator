<#
.SYNOPSIS
    Automated tests for Claude Status (hook lifecycle, Worker Board logic, layout/DPI, worker focusing,
    installer / update / uninstall, privacy).

.DESCRIPTION
    Everything runs against temporary folders; your real %USERPROFILE%\.claude is never touched.
    -Live      also runs live-tests.ps1: opens real console and test windows and moves keyboard focus
               between them (about 30 seconds; don't type while it runs).
    -E2E       also runs one real headless `claude -p` prompt with the hooks pointed at a temporary install.
    -Browser   also checks the phone dashboard page in headless Chrome/Edge emulating Android phones
               (tests\phone-browser.mjs; needs Node.js 22+; screenshots go to -RenderDir).
    -RenderDir where to save PNG renders of the Worker Board at each DPI scale (for visual review).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -Live
#>
param([switch]$Live, [switch]$E2E, [switch]$Browser, [string]$RenderDir)

if ($PSVersionTable.PSEdition -eq 'Core') {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($Live) { $a += '-Live' }; if ($E2E) { $a += '-E2E' }; if ($Browser) { $a += '-Browser' }; if ($RenderDir) { $a += @('-RenderDir', $RenderDir) }
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') @a
    exit $LASTEXITCODE
}
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('claude-status-tests-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $RenderDir) { $RenderDir = Join-Path $tmp 'renders' }
$null = New-Item -ItemType Directory -Force $RenderDir

# ======================================================================================
Section 'Hook lifecycle (hook.ps1 run as Claude Code runs it: stdin JSON, child powershell.exe)'
# ======================================================================================
$hookDir = Join-Path $tmp 'hook'; $null = New-Item -ItemType Directory -Force $hookDir
Copy-Item (Join-Path $src 'hook.ps1') $hookDir
$hook = Join-Path $hookDir 'hook.ps1'
$sess = Join-Path $hookDir 'state\sessions'
$fakeClaude = $PID   # a real, long-lived process standing in for claude.exe

function Invoke-Hook([string]$action, $in, [string]$claudePid = "$fakeClaude") {
    $old = $env:CLAUDE_PID
    if ($claudePid) { $env:CLAUDE_PID = $claudePid } else { Remove-Item Env:CLAUDE_PID -ErrorAction SilentlyContinue }
    try {
        $json = if ($in -is [string]) { $in } else { $in | ConvertTo-Json -Compress -Depth 5 }
        $out = $json | & $winPs -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $hook $action
        return @{ Out = ($out -join "`n"); Code = $LASTEXITCODE }
    } finally { if ($null -ne $old) { $env:CLAUDE_PID = $old } else { Remove-Item Env:CLAUDE_PID -ErrorAction SilentlyContinue } }
}
function State([string]$sid) { $f = Join-Path $sess "$sid.json"; if (Test-Path $f) { Get-Content $f -Raw | ConvertFrom-Json } else { $null } }
function Raw([string]$sid) { Get-Content (Join-Path $sess "$sid.json") -Raw }
function Ev([string]$sid, [string]$evt, $extra = @{}) {
    $h = @{ session_id = $sid; hook_event_name = $evt; cwd = 'C:\Users\Someone\Private Stuff\Toah_Nipi_Portal' }
    foreach ($k in $extra.Keys) { $h[$k] = $extra[$k] }
    return $h
}

$secretIn = Ev 's1' 'UserPromptSubmit' @{ prompt = 'SECRET-PROMPT-7731 please refactor'; transcript_path = 'C:\Users\Someone\.claude\projects\x\SECRET-TRANSCRIPT.jsonl' }
$r = Invoke-Hook 'working' $secretIn
$s = State 's1'
Check 'UserPromptSubmit -> WORKING' ($s.state -eq 'WORKING')
Check 'hook prints nothing to stdout and exits 0' ($r.Out -eq '' -and $r.Code -eq 0) "out='$($r.Out)' code=$($r.Code)"
Check 'schema v2 with state_since/first_seen/process identity' ($s.v -eq 2 -and $s.state_since -and $s.first_seen -and $s.claude_pid -eq $fakeClaude)
Check 'new session: state_since = first_seen = updated' ($s.state_since -eq $s.updated -and $s.first_seen -eq $s.updated)
Check 'claude_started matches the real process start time' ([Math]::Abs(([DateTime]::Parse($s.claude_started).ToUniversalTime() - (StartOf $fakeClaude)).TotalMilliseconds) -lt 1)
Check 'project is only the folder name' ($s.project -eq 'Toah_Nipi_Portal')
$allowed = 'v,session_id,state,event,detail,project,updated,state_since,first_seen,claude_pid,claude_started'
Check 'state file has exactly the documented fields' ((($s.PSObject.Properties.Name) -join ',') -eq $allowed) (($s.PSObject.Properties.Name) -join ',')
$raw = Raw 's1'
Check 'privacy: prompt text not stored' ($raw -notmatch 'SECRET-PROMPT')
Check 'privacy: transcript path / full cwd not stored' ($raw -notmatch 'SECRET-TRANSCRIPT' -and $raw -notmatch 'Private' -and $raw -notmatch 'Users')

$first = $s.first_seen; $since0 = $s.state_since
Start-Sleep -Milliseconds 1100
$null = Invoke-Hook 'working' (Ev 's1' 'UserPromptSubmit' @{ prompt = 'another' })
$s = State 's1'
Check 'same-state event keeps the WORKING timer (state_since unchanged)' ($s.state_since -eq $since0 -and $s.updated -ne $since0)

$null = Invoke-Hook 'needs_you' (Ev 's1' 'PermissionRequest' @{ tool_name = 'Bash'; tool_input = @{ command = 'rm -rf SECRET-CMD-991' } })
$s = State 's1'
Check 'PermissionRequest -> NEEDS_YOU with detail=tool name' ($s.state -eq 'NEEDS_YOU' -and $s.detail -eq 'Bash')
Check 'WORKING -> NEEDS_YOU resets the timer' ($s.state_since -ne $since0 -and $s.state_since -eq $s.updated)
Check 'privacy: tool input / bash command not stored' ((Raw 's1') -notmatch 'SECRET-CMD')
$nySince = $s.state_since
Start-Sleep -Milliseconds 300
$null = Invoke-Hook 'needs_you' (Ev 's1' 'Notification' @{ notification_type = 'permission_prompt'; message = 'SECRET-MSG Claude needs permission' })
$s = State 's1'
Check 'repeated NEEDS_YOU (Notification) keeps its timer' ($s.state_since -eq $nySince -and $s.detail -eq 'permission_prompt')
Check 'privacy: notification message not stored' ((Raw 's1') -notmatch 'SECRET-MSG')

Start-Sleep -Milliseconds 300
$null = Invoke-Hook 'resume' (Ev 's1' 'PostToolUse' @{ tool_name = 'Bash'; tool_response = 'SECRET-OUTPUT' })
$s = State 's1'
Check 'PostToolUse after NEEDS_YOU -> WORKING, timer reset' ($s.state -eq 'WORKING' -and $s.state_since -ne $nySince)
Check 'privacy: tool output not stored' ((Raw 's1') -notmatch 'SECRET-OUTPUT')
$u = $s.updated
$null = Invoke-Hook 'resume' (Ev 's1' 'PostToolUse')
Check 'PostToolUse while WORKING writes nothing' ((State 's1').updated -eq $u)

$null = Invoke-Hook 'done' (Ev 's1' 'Stop' @{ last_assistant_message = 'SECRET-REPLY all done' })
$s = State 's1'
Check 'Stop -> DONE' ($s.state -eq 'DONE')
Check 'privacy: Claude response not stored' ((Raw 's1') -notmatch 'SECRET-REPLY')
Check 'first_seen stable across the whole lifecycle' ($s.first_seen -eq $first)
$null = Invoke-Hook 'resume' (Ev 's1' 'PostToolUseFailure')
Check 'PostToolUseFailure never overwrites DONE' ((State 's1').state -eq 'DONE')

$before = Raw 's1'
$null = Invoke-Hook 'end' (Ev 's1' 'SessionEnd' @{ reason = 'prompt_input_exit' })
Check 'DONE -> SessionEnd: retained, unchanged' ((Test-Path (Join-Path $sess 's1.json')) -and (Raw 's1') -eq $before)

foreach ($case in @(@('working', 'WORKING'), @('needs_you', 'NEEDS_YOU'), @('error', 'ERROR'))) {
    $sid = 'end-' + $case[1]
    $null = Invoke-Hook $case[0] (Ev $sid 'x' @{ error = 'rate_limit' })
    $null = Invoke-Hook 'end' (Ev $sid 'SessionEnd')
    Check "$($case[1]) -> SessionEnd: removed" (-not (Test-Path (Join-Path $sess "$sid.json")))
}

# DONE dismissed (the indicator deletes the file) -> SessionEnd -> nothing; -> new prompt -> WORKING again
Remove-Item (Join-Path $sess 's1.json')
$null = Invoke-Hook 'end' (Ev 's1' 'SessionEnd')
Check 'dismissed DONE -> SessionEnd: stays gone' (-not (Test-Path (Join-Path $sess 's1.json')))
$null = Invoke-Hook 'working' (Ev 's1' 'UserPromptSubmit')
$s = State 's1'
Check 'dismissed session -> new prompt: reappears as WORKING' ($s.state -eq 'WORKING' -and $s.first_seen -ne $first)

$null = Invoke-Hook 'error' (Ev 'e1' 'StopFailure' @{ error = 'rate_limit'; error_details = 'SECRET-API-BODY {"message":"x"}' })
Check 'StopFailure -> ERROR with safe error type' ((State 'e1').state -eq 'ERROR' -and (State 'e1').detail -eq 'rate_limit')
Check 'privacy: API error details not stored' ((Raw 'e1') -notmatch 'SECRET-API')
$null = Invoke-Hook 'error' (Ev 'e2' 'StopFailure' @{ error = 'Weird message: with spaces & {json} and a very very long tail ...' })
Check 'error detail sanitised (no spaces/punctuation, <= 40 chars)' ((State 'e2').detail -match '^[A-Za-z0-9_.:-]{0,40}$') (State 'e2').detail

# Old v1 file from the previous version -> transitions to v2 naturally
$t0 = (Get-Date).ToUniversalTime().AddMinutes(-7).ToString('o')
[IO.File]::WriteAllText((Join-Path $sess 'old.json'), "{`"v`":1,`"session_id`":`"old`",`"state`":`"WORKING`",`"event`":`"UserPromptSubmit`",`"detail`":`"`",`"project`":`"Website`",`"updated`":`"$t0`"}")
$null = Invoke-Hook 'working' (Ev 'old' 'UserPromptSubmit')
$s = State 'old'
Check 'v1 file + same state -> v2, timer continues from v1 updated' ($s.v -eq 2 -and $s.state_since -eq $t0 -and $s.first_seen)
$null = Invoke-Hook 'done' (Ev 'old' 'Stop')
Check 'v1-upgraded session -> DONE resets timer' ((State 'old').state_since -ne $t0)

$null = Invoke-Hook 'working' (Ev 'nopid' 'UserPromptSubmit')
$null = Invoke-Hook 'done' (Ev 'nopid' 'Stop') -claudePid ''
Check 'missing CLAUDE_PID reuses the recorded process identity' ((State 'nopid').claude_pid -eq $fakeClaude)
$null = Invoke-Hook 'working' (Ev 'deadpid' 'UserPromptSubmit') -claudePid '999999'
Check 'unknown CLAUDE_PID is not recorded' ((State 'deadpid').claude_pid -eq 0 -and (State 'deadpid').claude_started -eq '')

$r = Invoke-Hook 'working' 'this is not json {{{'
Check 'garbage stdin: exits 0, prints nothing' ($r.Code -eq 0 -and $r.Out -eq '')
$sw = [Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt 3; $i++) { $null = Invoke-Hook 'working' (Ev 'perf' 'UserPromptSubmit') }
Info ("hook run time ~{0:N0} ms per call (powershell.exe start-up included)" -f ($sw.ElapsedMilliseconds / 3))
Check 'no temp files left behind' (@(Get-ChildItem $sess -Filter *.tmp).Count -eq 0)

# ======================================================================================
Section 'Worker Board logic (aliases, numbering, sorting, timers, pruning, placement)'
# ======================================================================================
$B = [ClaudeStatus.Board]
$now = [DateTime]::UtcNow
function Sess([string]$id, [string]$st, [string]$proj, [int]$sinceSec, [int]$firstMin, [int]$updSec = -1) {
    $si = New-Object ClaudeStatus.SessionInfo
    $si.Id = $id; $si.State = $st; $si.Project = $proj
    $si.StateSince = $now.AddSeconds(-$sinceSec); $si.Updated = if ($updSec -ge 0) { $now.AddSeconds(-$updSec) } else { $si.StateSince }
    $si.FirstSeen = $now.AddMinutes(-$firstMin)
    $si.Name = $B::FriendlyName($proj, $null)
    return $si
}
function L { $l = New-Object 'System.Collections.Generic.List[ClaudeStatus.SessionInfo]'; foreach ($x in $args) { foreach ($y in @($x)) { $l.Add($y) } }; return , $l }
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$aliases = $ser.Deserialize((Get-Content (Join-Path $src 'config.json') -Raw), [System.Collections.Generic.Dictionary[string, object]])['projectAliases']

Check 'alias: Toah_Nipi_Booking_Software -> Booking Software' ($B::FriendlyName('Toah_Nipi_Booking_Software', $aliases) -eq 'Booking Software')
Check 'alias: Toah_Nipi_Website -> Website' ($B::FriendlyName('Toah_Nipi_Website', $aliases) -eq 'Website')
Check 'alias: Toah_Nipi_Portal -> Portal' ($B::FriendlyName('Toah_Nipi_Portal', $aliases) -eq 'Portal')
Check 'alias: Guest_Booking_Form -> Guest Form' ($B::FriendlyName('Guest_Booking_Form', $aliases) -eq 'Guest Form')
Check 'alias lookup is case-insensitive' ($B::FriendlyName('toah_nipi_portal', $aliases) -eq 'Portal')
Check 'fallback: underscores -> spaces' ($B::FriendlyName('My_Cool__Project_', $aliases) -eq 'My Cool Project')
Check 'fallback: plain names unchanged' ($B::FriendlyName('DictaKey', $aliases) -eq 'DictaKey' -and $B::FriendlyName('AI-Status-Indicator', $aliases) -eq 'AI-Status-Indicator')
Check 'fallback: empty folder -> Claude Code' ($B::FriendlyName('', $aliases) -eq 'Claude Code')

# Parsing old (v1) and new (v2) state files
$d1 = $ser.Deserialize('{"v":1,"session_id":"a","state":"DONE","event":"Stop","detail":"","project":"Website","updated":"2026-10-20T10:00:00.0000000Z"}', [System.Collections.Generic.Dictionary[string, object]])
$p1 = $B::Parse('a', $d1, [DateTime]::Parse('2026-10-20T09:00:00Z').ToUniversalTime(), [DateTime]::MinValue)
Check 'v1 parse: state_since inferred from updated' ($p1.StateSince -eq [DateTime]::Parse('2026-10-20T10:00:00Z').ToUniversalTime() -and $p1.Version -eq 1)
Check 'v1 parse: first_seen from file creation time, no process identity' ($p1.FirstSeen -eq [DateTime]::Parse('2026-10-20T09:00:00Z').ToUniversalTime() -and $p1.ClaudePid -eq 0)
$d2 = $ser.Deserialize('{"v":2,"state":"WORKING","updated":"bad-date","claude_pid":"12","claude_started":""}', [System.Collections.Generic.Dictionary[string, object]])
$p2 = $B::Parse('b', $d2, [DateTime]::MinValue, $now)
Check 'bad dates fall back safely; pid without start time is ignored' ($p2.Updated -eq $now -and $p2.ClaudePid -eq 0)

# Sorting
$ny = Sess 'ny' 'NEEDS_YOU' 'Toah_Nipi_Portal' 120 50
$er = Sess 'er' 'ERROR' 'Analytics' 180 70
$w1 = Sess 'w1' 'WORKING' 'Booking' 272 40
$w2 = Sess 'w2' 'WORKING' 'Booking' 68 30
$dn = Sess 'dn' 'DONE' 'Website' 360 60 5
$order = { param($l) ($l | ForEach-Object { $_.Id }) -join ',' }
$ok = $true
for ($i = 0; $i -lt 10; $i++) { $l = L (@($dn, $w1, $ny, $w2, $er) | Get-Random -Count 5); $B::Sort($l, 'urgency'); if ((& $order $l) -ne 'ny,er,w2,w1,dn') { $ok = $false } }
Check 'urgency: NEEDS YOU, ERROR, WORKING, DONE; newest state first; deterministic' $ok
$l = L $ny $er $w1 $w2 $dn; $B::Sort($l, 'recent')
Check 'most recent: ordered by last update' ((& $order $l) -eq 'dn,w2,ny,er,w1') (& $order $l)
$t1 = Sess 't1' 'WORKING' 'X' 60 10; $t2 = Sess 't2' 'WORKING' 'X' 60 20
$l = L $t1 $t2; $B::Sort($l, 'urgency')
Check 'ties broken by first seen' ((& $order $l) -eq 't2,t1')
$cw = Sess 'cw' 'WORKING' 'Booking' 10 3 0; $cn = Sess 'cn' 'NEEDS_YOU' 'Portal' 300 5 300; $cd = Sess 'cd' 'DONE' 'Website' 100 9 100
Check 'Compact (urgency) surfaces NEEDS YOU' (($B::Surface((L $cw $cn $cd), 'urgency')).Id -eq 'cn')
Check 'Compact (most recent) surfaces the latest' (($B::Surface((L $cw $cn $cd), 'recent')).Id -eq 'cw')
Check 'Compact with no sessions surfaces nothing' ($null -eq $B::Surface((L), 'urgency'))

# Numbering
$num = New-Object ClaudeStatus.WorkerNumbering
$b1 = Sess 'b1' 'WORKING' 'Toah_Nipi_Booking_Software' 10 30; $b2 = Sess 'b2' 'WORKING' 'Toah_Nipi_Booking_Software' 10 20; $b3 = Sess 'b3' 'NEEDS_YOU' 'Toah_Nipi_Booking_Software' 10 10
$web = Sess 'web' 'DONE' 'Toah_Nipi_Website' 10 5
foreach ($x in $b1, $b2, $b3, $web) { $x.Name = $B::FriendlyName($x.Project, $aliases) }
$num.Apply((L $b3 $web $b1 $b2))
Check 'three workers in one project: #1 #2 #3 by first seen' ($b1.DisplayName -eq 'Booking Software #1' -and $b2.DisplayName -eq 'Booking Software #2' -and $b3.DisplayName -eq 'Booking Software #3')
Check 'single worker in a project: no #1' ($web.DisplayName -eq 'Website')
$b1.State = 'DONE'; $b2.State = 'NEEDS_YOU'; $b3.State = 'WORKING'
$stable = $true
for ($i = 0; $i -lt 5; $i++) { $num.Apply((L (@($b1, $b2, $b3, $web) | Get-Random -Count 4))); if ($b1.Number -ne 1 -or $b2.Number -ne 2 -or $b3.Number -ne 3) { $stable = $false } }
Check 'numbers stay put through state changes and re-sorting' $stable
$num.Apply((L $b2 $b3 $web))
Check 'one worker leaving does not renumber the others' ($b2.Number -eq 2 -and $b3.Number -eq 3)
$b4 = Sess 'b4' 'WORKING' 'Toah_Nipi_Booking_Software' 1 1; $b4.Name = 'Booking Software'
$num.Apply((L $b2 $b3 $web $b4))
Check 'a newcomer takes the lowest free number' ($b4.Number -eq 1 -and $b2.Number -eq 2 -and $b3.Number -eq 3)
$num.Apply((L $b3 $web))
Check 'back to one worker: number dropped' ($b3.DisplayName -eq 'Booking Software')

# Timers / labels
$TS = { param($s) [TimeSpan]::FromSeconds($s) }
$dot = [char]0xB7
Check 'WORKING clock 04:32' ($B::Clock((& $TS 272)) -eq '04:32')
Check 'WORKING clock 1h 05m / 2d 3h' ($B::Clock((& $TS 3900)) -eq '1h 05m' -and $B::Clock((& $TS (2 * 86400 + 3 * 3600))) -eq '2d 3h')
Check 'waiting span <1m / 2m / 1h 5m / 3d' ($B::Span((& $TS 30)) -eq '<1m' -and $B::Span((& $TS 125)) -eq '2m' -and $B::Span((& $TS 3900)) -eq '1h 5m' -and $B::Span((& $TS (3 * 86400))) -eq '3d')
Check 'DONE ago: just now / 6m ago / 2h ago / 3d ago' ($B::Ago((& $TS 20)) -eq 'just now' -and $B::Ago((& $TS 370)) -eq '6m ago' -and $B::Ago((& $TS 7300)) -eq '2h ago' -and $B::Ago((& $TS (3 * 86400 + 5))) -eq '3d ago')
$es = Sess 'x' 'ERROR' 'A' 180 1; $es.Detail = 'rate_limit'
Check 'ERROR status shows safe error type' ($B::StatusText($es, $now, $false) -eq "ERROR $dot rate_limit $dot 3m") $B::StatusText($es, $now, $false)
Check 'ERROR brief form drops the type' ($B::StatusText($es, $now, $true) -eq "ERROR $dot 3m")
$es.Detail = 'authentication_failed'
Check 'long error types abbreviated' ($B::StatusText($es, $now, $false) -match ' auth ')
Check 'NEEDS YOU / WORKING / DONE labels' ($B::StatusText((Sess 'n' 'NEEDS_YOU' 'A' 125 1), $now, $false) -eq "NEEDS YOU $dot 2m" -and $B::StatusText((Sess 'w' 'WORKING' 'A' 272 1), $now, $false) -eq "WORKING $dot 04:32" -and $B::StatusText((Sess 'd' 'DONE' 'A' 370 1), $now, $false) -eq "DONE $dot 6m ago")
Check 'clock skew (state_since in the future) shows 00:00' ($B::StatusText((Sess 'f' 'WORKING' 'A' -30 1), $now, $false) -eq "WORKING $dot 00:00")
Check 'summary text' ($B::Summary((L $w1 $w2 $ny $dn $dn)) -eq "1 needs you $dot 2 working $dot 2 done")

# Pruning
$old = Sess 'o' 'DONE' 'A' 999999 99999 999999
Check 'DONE never auto-removed (even 11 days old, process gone)' (-not $B::ShouldPrune($old, $now, 24, 0))
Check 'WORKING older than staleHours removed' ($B::ShouldPrune((Sess 'o' 'WORKING' 'A' 0 1 (25 * 3600)), $now, 24, -1))
Check 'WORKING whose Claude process is gone removed' ($B::ShouldPrune((Sess 'o' 'WORKING' 'A' 0 1 0), $now, 24, 0))
Check 'ERROR whose Claude process is gone removed' ($B::ShouldPrune((Sess 'o' 'ERROR' 'A' 0 1 0), $now, 24, 0))
Check 'NEEDS YOU with running / unknown process kept' (-not $B::ShouldPrune((Sess 'o' 'NEEDS_YOU' 'A' 0 1 0), $now, 24, 1) -and -not $B::ShouldPrune((Sess 'o' 'NEEDS_YOU' 'A' 0 1 0), $now, 24, -1))

# Placement (the working area already excludes the taskbar)
$wa = New-Object System.Drawing.Rectangle(0, 0, 1920, 1040)
$r = $B::Place((New-Object System.Drawing.Point(100, 100)), (New-Object System.Drawing.Size(276, 200)), $wa, 120)
Check 'fits: placed at its remembered position' ($r.X -eq 100 -and $r.Y -eq 100 -and $r.Height -eq 200)
$r = $B::Place((New-Object System.Drawing.Point(100, 600)), (New-Object System.Drawing.Size(276, 900)), $wa, 120)
Check 'tall: grows to the bottom of the working area, then scrolls' ($r.Y -eq 600 -and $r.Bottom -eq 1040 -and $r.Height -lt 900)
$r = $B::Place((New-Object System.Drawing.Point(100, 1000)), (New-Object System.Drawing.Size(276, 400)), $wa, 120)
Check 'parked near the bottom: moved up just enough to show the minimum' ($r.Bottom -eq 1040 -and $r.Height -eq 120)
$r = $B::Place((New-Object System.Drawing.Point(1800, 50)), (New-Object System.Drawing.Size(276, 200)), $wa, 120)
Check 'past the right edge: pulled back on screen' ($r.Right -eq 1920)
$r = $B::Place((New-Object System.Drawing.Point(100, 100)), (New-Object System.Drawing.Size(276, 5000)), $wa, 5000)
Check 'never taller than the working area' ($r.Top -ge 0 -and $r.Bottom -le 1040)
$topBar = New-Object System.Drawing.Rectangle(0, 48, 1920, 1032)
$r = $B::Place((New-Object System.Drawing.Point(100, 10)), (New-Object System.Drawing.Size(276, 200)), $topBar, 120)
Check 'taskbar at the top: kept below it' ($r.Top -eq 48)
$left = New-Object System.Drawing.Rectangle(-1920, 0, 1920, 1040)
$r = $B::Place((New-Object System.Drawing.Point(-500, 900)), (New-Object System.Drawing.Size(276, 400)), $left, 120)
Check 'second monitor at negative coordinates' ($r.X -eq -500 -and $r.Bottom -eq 1040 -and $r.Left -ge -1920)

# Process chain (PID reuse protection)
$starts = @{ 10 = [DateTime]'2026-01-01T10:00:00'; 20 = [DateTime]'2026-01-01T09:00:00'; 30 = [DateTime]'2026-01-01T11:00:00'; 40 = [DateTime]'2026-01-01T08:00:00' }
$parents = @{ 10 = 20; 20 = 30; 30 = 40 }
$chain = [ClaudeStatus.WorkerWindows]::Chain(10, [Func[int, int]] { param($p) if ($parents.ContainsKey($p)) { $parents[$p] } else { 0 } }, [Func[int, DateTime]] { param($p) if ($starts.ContainsKey($p)) { $starts[$p] } else { [DateTime]::MinValue } })
Check 'ancestor walk stops at a parent that started after its child (reused PID)' ((@($chain) -join ',') -eq '10,20') (@($chain) -join ',')
$parents = @{ 10 = 20; 20 = 10 }
$chain = [ClaudeStatus.WorkerWindows]::Chain(10, [Func[int, int]] { param($p) $parents[$p] }, [Func[int, DateTime]] { param($p) [DateTime]'2026-01-01' })
Check 'ancestor walk survives cycles' (@($chain).Count -le 2)

# ======================================================================================
Section 'Indicator window (Worker Board + Compact) against temporary state folders'
# ======================================================================================
$root = New-Root
$f = Form $root
Check 'no config preference -> Worker Board is the default view' $f.IsBoard
Check 'empty board: one "No Claude workers" row, compact height' ($f.Workers.Count -eq 0 -and $f.ClientSize.Height -lt 70) $f.ClientSize.Height
Check 'empty: tray says IDLE (with the app name)' ($f.TrayText -eq 'Claude Status: IDLE')
$f.Dispose()
$f = Form (New-Root -NoConfig); Check 'missing config.json -> Worker Board' $f.IsBoard; $f.Dispose()
$f = Form (New-Root @{ startupView = 'compact' }); Check 'startupView=compact -> Compact' (-not $f.IsBoard); Check 'Compact keeps its original 124x38 size' ($f.ClientSize.Width -eq 124 -and $f.ClientSize.Height -eq 38); $f.Dispose()

$root = New-Root
$me = StartOf $PID
Put $root 'one' 'WORKING' 'Toah_Nipi_Booking_Software' 272 10 $PID $me
$f = Form $root
Check 'one WORKING row with alias and timer' ($f.Workers.Count -eq 1 -and $f.Workers[0].DisplayName -eq 'Booking Software')
$g = [System.Drawing.Graphics]::FromImage((New-Object System.Drawing.Bitmap 10, 10))
Check 'row status text WORKING - 04:3x' ($f.RowStatus(0, $g) -match "^WORKING $dot 04:3\d$") $f.RowStatus(0, $g)
$f.Dispose()

$root = New-Root
Put $root 'ny' 'NEEDS_YOU' 'Toah_Nipi_Portal' 130 50 $PID $me
Put $root 'er' 'ERROR' 'Analytics' 200 40 $PID $me
Put $root 'b1' 'WORKING' 'Toah_Nipi_Booking_Software' 272 30 $PID $me
Put $root 'b2' 'WORKING' 'Toah_Nipi_Booking_Software' 68 20 $PID $me
Put $root 'web' 'DONE' 'Toah_Nipi_Website' 360 60 -updSec 360
Put $root 'v1' 'DONE' 'Guest_Booking_Form' 7300 0 -v 1
$f = Form $root
$f.Home = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Location   # room to grow
Check 'mixed board in urgency order' ((Ids $f) -eq 'ny,er,b2,b1,web,v1') (Ids $f)
Check 'names: Portal, Booking Software #1/#2, Website, Guest Form (from a v1 file)' ((($f.Workers | ForEach-Object { $_.DisplayName }) -join '|') -eq 'Portal|Analytics|Booking Software #2|Booking Software #1|Website|Guest Form')
Check 'Compact/tray surface the most urgent worker' ($f.Surfaced.Id -eq 'ny' -and $f.TrayText -eq 'Claude Status: NEEDS YOU (Portal)')
$f.SetSetting('workerSort', 'recent')
Check 'switching to Most recent re-sorts immediately' ((Ids $f) -eq 'b2,ny,er,b1,web,v1') (Ids $f)
Check 'Compact follows Most recent' ($f.Surfaced.Id -eq 'b2')
$saved = Get-Content (Join-Path $root 'config.json') -Raw | ConvertFrom-Json
Check 'setting persisted; other settings and aliases preserved' ($saved.workerSort -eq 'recent' -and $saved.soundOnDone -eq $true -and $saved.staleHours -eq 24 -and $saved.projectAliases.Toah_Nipi_Portal -eq 'Portal')
$h0 = $f.ClientSize.Height
$f.SetSetting('showWorkerSummary', $true)
Check 'summary on: board grows by one small line' ($f.ClientSize.Height -gt $h0 -and $f.ClientSize.Height - $h0 -le 20)
$f.SetSetting('showWorkerSummary', $false)
Check 'summary off: no space reserved' ($f.ClientSize.Height -eq $h0)
$f.Dispose()

# Restart keeps DONE rows; stale / dead active rows are cleaned up
$root = New-Root
Put $root 'olddone' 'DONE' 'Website' (30 * 3600) 9999 99999 ([DateTime]'2020-01-01') -updSec (30 * 3600)
Put $root 'stale' 'WORKING' 'Portal' (30 * 3600) 9999 -updSec (30 * 3600)
$dead = Start-Process $winPs -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Milliseconds 1500' -PassThru -WindowStyle Hidden
$deadStart = StartOf $dead.Id; $dead.WaitForExit()
Put $root 'crashed' 'WORKING' 'Portal' 10 5 $dead.Id $deadStart
Put $root 'reused' 'NEEDS_YOU' 'Portal' 10 5 $PID ($me.AddMinutes(-3))
Put $root 'alive' 'WORKING' 'Portal' 10 5 $PID $me
$f = Form $root
Check 'DONE retained across restart (30h old, staleHours=24, process gone)' (($f.Workers | Where-Object Id -eq 'olddone') -and (Test-Path (Join-Path $root 'state\sessions\olddone.json')))
Check 'stale WORKING (30h) removed' (-not (Test-Path (Join-Path $root 'state\sessions\stale.json')))
Check 'WORKING whose Claude process exited removed' (-not (Test-Path (Join-Path $root 'state\sessions\crashed.json')))
Check 'PID reused by another process (start time differs) treated as gone' (-not (Test-Path (Join-Path $root 'state\sessions\reused.json')))
Check 'running worker kept' ($null -ne ($f.Workers | Where-Object Id -eq 'alive'))

# Dismiss
Check 'dismiss is refused for a WORKING row' (-not $f.Dismiss('alive') -and (Test-Path (Join-Path $root 'state\sessions\alive.json')))
Check 'dismiss a DONE row: removed from board and disk' ($f.Dismiss('olddone') -and -not (Test-Path (Join-Path $root 'state\sessions\olddone.json')) -and -not ($f.Workers | Where-Object Id -eq 'olddone'))
$f.Dispose()
$f = Form $root
Check 'dismissal persists across restart' (-not ($f.Workers | Where-Object Id -eq 'olddone'))
Put $root 'olddone' 'WORKING' 'Website' 1 0 $PID $me
$f.ReloadNow()
Check 'dismissed session prompting again reappears' (($f.Workers | Where-Object Id -eq 'olddone').State -eq 'WORKING')
Put $root 'race' 'DONE' 'Website' 5 1
$f.ReloadNow()
Put $root 'race' 'WORKING' 'Website' 0 1 $PID $me
Check 'dismiss loses the race safely when a new prompt just arrived' (-not $f.Dismiss('race') -and (Test-Path (Join-Path $root 'state\sessions\race.json')))
$f.Dispose()

# Number stability inside the running indicator
$root = New-Root
foreach ($i in 1..3) { Put $root "bk$i" 'WORKING' 'Toah_Nipi_Booking_Software' (100 * $i) (40 - $i) $PID $me }
$f = Form $root
$n1 = ($f.Workers | Sort-Object Id | ForEach-Object { $_.DisplayName }) -join '|'
Put $root 'bk1' 'NEEDS_YOU' 'Toah_Nipi_Booking_Software' 1 39 $PID $me
Put $root 'bk3' 'DONE' 'Toah_Nipi_Booking_Software' 1 37
$f.ReloadNow()
$n2 = ($f.Workers | Sort-Object Id | ForEach-Object { $_.DisplayName }) -join '|'
Check 'Booking Software #1/#2/#3 stay attached to the same sessions' ($n1 -eq 'Booking Software #1|Booking Software #2|Booking Software #3' -and $n2 -eq $n1) "$n1 / $n2"
Remove-Item (Join-Path $root 'state\sessions\bk1.json'); $f.ReloadNow()
Check 'removing #1 leaves #2 and #3 unchanged' ((($f.Workers | Sort-Object Id | ForEach-Object { $_.DisplayName }) -join '|') -eq 'Booking Software #2|Booking Software #3')
$f.Dispose()

# Corrupt files never crash the indicator; a broken config.json is never overwritten
$root = New-Root
[IO.File]::WriteAllText((Join-Path $root 'state\sessions\junk.json'), '{not json')
[IO.File]::WriteAllText((Join-Path $root 'state\sessions\empty.json'), '')
$f = Form $root
Check 'corrupt / empty state files are ignored' ($f.Workers.Count -eq 0)
[IO.File]::WriteAllText((Join-Path $root 'config.json'), '{ "soundOnDone": tru')
$f.SetSetting('workerSort', 'recent')
Check 'settings are not saved over a broken config.json' ((Get-Content (Join-Path $root 'config.json') -Raw) -eq '{ "soundOnDone": tru')
$f.Dispose()

# Settings window (built but not shown, so the test doesn't take focus) and the View menu
$changes = @{}
$sf = New-Object ClaudeStatus.SettingsForm('urgency', $false, 'workerBoard', [Action[string, object]] { param($k, $v) $changes[$k] = $v }, [Action] { })
$null = $sf.Handle; $sf.PerformLayout()
$ctrls = @(); $stack = New-Object System.Collections.Stack; $stack.Push($sf)
while ($stack.Count) { $c = $stack.Pop(); $ctrls += $c; foreach ($k in $c.Controls) { $stack.Push($k) } }
$radio = { param($t) $ctrls | Where-Object { $_ -is [System.Windows.Forms.RadioButton] -and $_.Text -like "$t*" } | Select-Object -First 1 }
Check 'settings window starts with Urgency / summary off / Worker Board selected' ((& $radio 'Urgency').Checked -and -not (& $radio 'Most recent').Checked -and (& $radio 'Worker Board').Checked -and -not ($ctrls | Where-Object { $_ -is [System.Windows.Forms.CheckBox] -and $_.Text -eq 'Show worker summary' }).Checked)
(& $radio 'Most recent').Checked = $true
($ctrls | Where-Object { $_ -is [System.Windows.Forms.CheckBox] -and $_.Text -eq 'Show worker summary' }).Checked = $true
(& $radio 'Compact').Checked = $true
Check 'settings window: ordering / summary / startup view apply immediately' ($changes['workerSort'] -eq 'recent' -and $changes['showWorkerSummary'] -eq $true -and $changes['startupView'] -eq 'compact')
Check 'settings window stays small' ($sf.Width -lt 0.4 * [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Width -and $sf.Height -lt 0.6 * [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Height) "$($sf.Size)"
$sf.Dispose()
$f = Form (New-Root)
$f.GetType().GetMethod('BuildMenu', [Reflection.BindingFlags]'NonPublic,Instance').Invoke($f, @())
$menu = $f.GetType().GetField('menu', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($f)
$view = $menu.Items | Where-Object { $_.Text -eq 'View' }
Check 'right-click menu: View > Worker Board (checked) / Compact' ($view -and $view.DropDownItems[0].Text -eq 'Worker Board' -and $view.DropDownItems[0].Checked -and $view.DropDownItems[1].Text -eq 'Compact' -and -not $view.DropDownItems[1].Checked)
$texts = @($menu.Items | ForEach-Object { $_.Text })
Check 'menu keeps Settings, Show window, Reset position, Exit; no row actions' (($texts -like 'Settings*') -and ($texts -contains 'Show window') -and ($texts -contains 'Reset position') -and ($texts -contains 'Exit') -and -not ($texts -match 'Kill|Stop|Restart|Dismiss'))
$trayTexts = @($f.TrayMenu.Items | ForEach-Object { $_.Text })
Check 'tray icon has its own small menu: Open, Quit' (($trayTexts -join ',') -eq 'Open,Quit')
$hb = $f.HeaderButtons
Check 'Worker Board header: minimize sits between settings and close' ($hb.Count -eq 4 -and $f.HitAt((New-Object System.Drawing.Point(($hb[2].Left + 9), ($hb[2].Top + 9)))) -eq 'minimize' -and $hb[2].Left -gt $hb[1].Right - 1 -and $hb[2].Right -le $hb[3].Left + 1)
Check 'tooltip: Minimize to tray' ($f.TooltipFor('minimize') -like 'Minimize to tray*')
$f.Dispose()

# Compact view still behaves as before
$root = New-Root @{ startupView = 'compact' }
Put $root 'p' 'WORKING' 'Toah_Nipi_Portal' 5 1 $PID $me
$f = Form $root
Check 'Compact single session shows its friendly name' ($f.Surfaced.DisplayName -eq 'Portal' -and -not $f.IsBoard)
Put $root 'q' 'DONE' 'Website' 5 2
$f.ReloadNow()
Check 'Compact with two sessions keeps WORKING ahead of DONE (urgency)' ($f.Surfaced.Id -eq 'p')
$bmp = New-Object System.Drawing.Bitmap($f.ClientSize.Width, $f.ClientSize.Height); $gg = [System.Drawing.Graphics]::FromImage($bmp); $f.PaintTo($gg); $gg.Dispose()
$bmp.Save((Join-Path $RenderDir 'compact-100.png')); $bmp.Dispose()
Check 'Compact hit areas: close / minimize / settings / view toggle / drag' ($f.HitAt((New-Object System.Drawing.Point(115, 8))) -eq 'close' -and $f.HitAt((New-Object System.Drawing.Point(99, 8))) -eq 'minimize' -and $f.HitAt((New-Object System.Drawing.Point(83, 8))) -eq 'gear' -and $f.HitAt((New-Object System.Drawing.Point(67, 8))) -eq 'toggle' -and $f.HitAt((New-Object System.Drawing.Point(30, 28))) -eq 'drag')
Check 'tooltips: Settings / Switch to Worker Board' ($f.TooltipFor('gear') -eq 'Settings' -and $f.TooltipFor('toggle') -eq 'Switch to Worker Board')
$f.Dispose()

# ======================================================================================
Section 'Layout at 100% / 125% / 150% / 200% DPI (1, 6 and 90 workers)'
# ======================================================================================
$long = 'A_Very_Long_Project_Folder_Name_That_Keeps_Going'
foreach ($scale in 1.0, 1.25, 1.5, 2.0) {
    foreach ($rows in 1, 6, 90) {
        $root = New-Root
        $states = 'NEEDS_YOU', 'ERROR', 'WORKING', 'DONE'
        for ($i = 0; $i -lt $rows; $i++) {
            $st = $states[$i % 4]
            $proj = if ($i -eq 1) { $long } else { 'Toah_Nipi_Booking_Software' }
            if ($st -eq 'DONE') { Put $root ("r{0:D2}" -f $i) $st $proj (60 * $i + 7300) $i } else { Put $root ("r{0:D2}" -f $i) $st $proj (60 * $i + 3) $i $PID $me }
        }
        Get-ChildItem (Join-Path $root 'state\sessions') | Where-Object { $_.Name -like 'r01*' } | ForEach-Object { $j = Get-Content $_.FullName -Raw | ConvertFrom-Json; $j.detail = 'authentication_failed'; [IO.File]::WriteAllText($_.FullName, ($j | ConvertTo-Json -Compress)) }
        $f = Form $root ([single]$scale)
        $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
        $f.Home = $wa.Location
        $tag = "{0}% x{1}" -f ($scale * 100), $rows
        $bmp = New-Object System.Drawing.Bitmap($f.ClientSize.Width, $f.ClientSize.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $statusFont = New-Object System.Drawing.Font('Segoe UI', (11 * $scale), [System.Drawing.GraphicsUnit]::Pixel)
        $cw = $f.ClientSize.Width
        $problems = @()
        if (-not $wa.Contains($f.Bounds)) { $problems += "window $($f.Bounds) outside working area $wa (taskbar)" }
        $btns = $f.HeaderButtons
        foreach ($bt in $btns) { if ($bt.Right -gt $cw -or $bt.Left -lt 0 -or $bt.Bottom -gt $f.ListViewport.Top) { $problems += "header button $bt clipped" } }
        for ($k = 0; $k -lt $btns.Count - 1; $k++) { if ($btns[$k].IntersectsWith($btns[$k + 1])) { $problems += 'header buttons overlap' } }
        if ($rows -eq 90) {
            if ($f.MaxScroll -le 0) { $problems += 'expected the list to scroll' }
            $f.ScrollOffset = $f.MaxScroll
        }
        $vp = $f.ListViewport
        for ($i = 0; $i -lt $rows; $i++) {
            $rr = $f.RowBounds($i)
            if ($rr.Bottom -le $vp.Top -or $rr.Top -ge $vp.Bottom) { continue }
            $nr = $f.NameBounds($i, $g); $sr = $f.StatusBounds($i, $g); $xr = $f.DismissBounds($i)
            $st = $f.RowStatus($i, $g)
            $need = [System.Windows.Forms.TextRenderer]::MeasureText($g, $st, $statusFont, [System.Drawing.Size]::Empty, [System.Windows.Forms.TextFormatFlags]'NoPadding, SingleLine').Width
            if ($need -gt $sr.Width) { $problems += "row $i status clipped ($st)" }
            if ($nr.IntersectsWith($sr)) { $problems += "row $i name overlaps status" }
            if ($nr.Width -lt [int](60 * $scale)) { $problems += "row $i name squeezed to $($nr.Width)px" }
            if ($sr.Right -gt $xr.Left) { $problems += "row $i status runs into the X" }
            if ($xr.Right -gt $cw - 1) { $problems += "row $i X outside window" }
            if ($f.MaxScroll -gt 0 -and $xr.Right -gt $cw - [int](7 * $scale)) { $problems += "row $i X under scrollbar" }
            $w = $f.Workers[$i]
            if ($w.State -eq 'DONE' -and $rr.Top -ge $vp.Top -and $rr.Bottom -le $vp.Bottom) {
                $c = New-Object System.Drawing.Point(($xr.Left + $xr.Width / 2), ($xr.Top + $xr.Height / 2))
                if ($f.HitAt($c) -ne "x:$($w.Id)") { $problems += "row $i X not clickable" }
            }
        }
        if ($rows -eq 90) {
            $last = $f.RowBounds($rows - 1)
            if ($last.Bottom -gt $vp.Bottom) { $problems += 'last row not reachable by scrolling' }
            if ($f.HitAt((New-Object System.Drawing.Point(($btns[1].Left + 3), ($btns[1].Top + 3)))) -ne 'gear') { $problems += 'header not usable while scrolled' }
        }
        $f.PaintTo($g); $g.Dispose()
        $bmp.Save((Join-Path $RenderDir ("board-{0}-{1}rows.png" -f ($scale * 100), $rows))); $bmp.Dispose()
        Check "layout $tag (no clipping, X/gear/toggle reachable, inside working area)" ($problems.Count -eq 0) ($problems -join '; ')
        if ($rows -eq 6 -and $scale -eq 1.0) {
            Check 'truncated long name has the full name in its tooltip' ($f.TooltipFor('row:r01') -match 'A Very Long Project Folder Name That Keeps Going')
        }
        $f.Dispose()
    }
}
$root = New-Root
for ($i = 0; $i -lt 12; $i++) { Put $root "n$i" 'WORKING' 'Portal' 5 $i $PID $me }
$f = Form $root
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$f.Home = New-Object System.Drawing.Point(($wa.Left + 50), ($wa.Bottom - 10))
Check 'dragged to the bottom edge: moved up just enough, stays above the taskbar' ($f.Bounds.Bottom -le $wa.Bottom -and $f.Workers.Count -eq 12 -and $f.ListViewport.Height -ge 4 * 22) "$($f.Bounds)"
Check 'remembered position is not overwritten by that adjustment' ($f.Home.Y -eq ($wa.Bottom - 10))
$f.Dispose()
Info "renders saved to $RenderDir"

# ======================================================================================
Section 'Worker focusing: process identity (no windows opened)'
# ======================================================================================
$WW = [ClaudeStatus.WorkerWindows]
$why = ''
Check 'no recorded identity (v1 file): refuses, explains' ($WW::Resolve(0, [DateTime]::MinValue, $null, [ref]$why) -eq [IntPtr]::Zero -and $why -match 'next prompt')
Check 'exited Claude process: refuses' ($WW::Resolve($dead.Id, $deadStart, $null, [ref]$why) -eq [IntPtr]::Zero -and $why -match 'no longer running') $why
Check 'PID reused (start time differs): refuses' ($WW::Resolve($PID, $me.AddSeconds(-5), $null, [ref]$why) -eq [IntPtr]::Zero -and $why -match 'no longer running') $why
Check 'process identity: running / gone / reused' ($WW::ProcessAlive($PID, $me) -eq 1 -and $WW::ProcessAlive($dead.Id, $deadStart) -eq 0 -and $WW::ProcessAlive($PID, $me.AddSeconds(1)) -eq 0)

if ($env:CLAUDE_PID -match '^\d+$' -and (Get-Process -Id $env:CLAUDE_PID -ErrorAction SilentlyContinue)) {
    $cp = [int]$env:CLAUDE_PID; $cs = StartOf $cp
    $hw = $WW::WorkerWindowsFor($cp)
    $res = $WW::Resolve($cp, $cs, $null, [ref]$why)
    $hostName = if ($hw.Count -gt 0) { (Get-Process -Id ([TNative]::WindowPid($hw[0]))).ProcessName } else { 'none' }
    Info "this Claude session's host: $hostName with $($hw.Count) window(s)"
    if ($hw.Count -gt 1) {
        Check 'real session in a multi-window app without an anchor: refuses (no guessing)' ($res -eq [IntPtr]::Zero -and $why -match 'windows open') $why
        $hp = [int][TNative]::WindowPid($hw[1])
        $a = New-Object ClaudeStatus.Anchor; $a.Hwnd = $hw[1].ToInt64(); $a.Pid = $hp; $a.Started = StartOf $hp
        Check 'real session with a verified anchor: resolves to that exact window' ($WW::Resolve($cp, $cs, $a, [ref]$why) -eq $hw[1])
    } elseif ($hw.Count -eq 1) {
        Check 'real session in a single-window app: resolves to it' ($res -eq $hw[0])
    } else { Skip 'real Claude session window' 'no host window found' }
} else { Skip 'real Claude session window' 'not running inside Claude Code' }

# ======================================================================================
Section 'Phone dashboard: server, parity, dismissal, races, request protection, lifecycle'
# ======================================================================================
Add-Type @'
using System; using System.IO; using System.Text; using System.Threading; using System.Threading.Tasks; using System.Net.Sockets;
public static class THttp {
    public static string Send(int port, string raw, int timeoutMs) {
        using (var c = new TcpClient()) {
            c.ReceiveTimeout = timeoutMs; c.Connect("127.0.0.1", port);
            var s = c.GetStream(); var b = Encoding.UTF8.GetBytes(raw); s.Write(b, 0, b.Length);
            using (var sr = new StreamReader(s, Encoding.UTF8)) return sr.ReadToEnd();
        }
    }
    public static Task<string> SendAsync(int port, string raw, int timeoutMs) { return Task.Run(() => Send(port, raw, timeoutMs)); }
    public static int Status(string r) { var p = r.Split(' '); int c; return p.Length > 1 && int.TryParse(p[1], out c) ? c : 0; }
    public static string Body(string r) { int i = r.IndexOf("\r\n\r\n"); return i < 0 ? "" : r.Substring(i + 4); }
    public static bool CanConnect(int port) { try { using (var c = new TcpClient()) { c.Connect("127.0.0.1", port); return true; } } catch { return false; } }
    public static int FreePort() { var l = new TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
}
public static class TFake {
    public static Func<string[]> Names(string[] n) { return () => n; }
}
// Races a hook-style rewrite (DONE -> WORKING: temp file + atomic replace, retrying on sharing errors, as
// hook.ps1 does) against a dismissal of the DONE occurrence, with random sub-millisecond offsets.
public static class TRace {
    static void Spin(long ticks) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedTicks < ticks) { } }
    static void HookWrite(string path, string json) {
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, json);
        for (int i = 0; i < 10; i++) { try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); break; } catch { Thread.Sleep(15); } }
        if (File.Exists(tmp)) File.Delete(tmp);
    }
    public static string Run(string path, string id, string doneJson, string workJson, string occ, Delegate dismiss, int iterations, int seed) {
        var rnd = new Random(seed); int lost = 0; var counts = new System.Collections.Generic.SortedDictionary<string, int>();
        for (int i = 0; i < iterations; i++) {
            File.WriteAllText(path, doneJson);
            long dw = rnd.Next(0, 6000), dd = rnd.Next(0, 6000);
            var go = new ManualResetEventSlim();
            var t = new Thread(() => { go.Wait(); Spin(dw); HookWrite(path, workJson); });
            t.Start(); Thread.Sleep(1);
            go.Set(); Spin(dd);
            string res = dismiss.DynamicInvoke(id, occ).ToString();
            t.Join();
            int n; counts.TryGetValue(res, out n); counts[res] = n + 1;
            string final = File.Exists(path) ? File.ReadAllText(path) : "";
            if (!final.Contains("\"WORKING\"")) lost++;
        }
        var sb = new StringBuilder("lost=" + lost);
        foreach (var kv in counts) sb.Append(";" + kv.Key + "=" + kv.Value);
        return sb.ToString();
    }
}
'@

# The server calls these on its own threads, so they must be real .NET delegates (not script blocks).
function Dismisser([string]$dir) { [Delegate]::CreateDelegate([Func[string, string, ClaudeStatus.DismissResult]], $dir, [ClaudeStatus.Dismissal].GetMethod('DismissDone')) }
$epoch = New-Object DateTime(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
$phoneFiles = 'phone.html', 'phone.css', 'phone.js'
function New-PhoneRoot([int]$port, [hashtable]$extra = @{}) {
    $o = @{ phoneDashboard = $true; phonePort = $port }; foreach ($k in $extra.Keys) { $o[$k] = $extra[$k] }
    $r = New-Root $o
    foreach ($fn in $phoneFiles) { Copy-Item (Join-Path $src $fn) $r }
    return $r
}
# Raw HTTP/1.1, so tests control every header (Host, Origin, ...). $h values of $null omit that header.
function RawReq([int]$port, [string]$method, [string]$path, [hashtable]$h = @{}, [string]$body = $null) {
    $lines = @("$method $path HTTP/1.1")
    if (-not $h.ContainsKey('Host')) { $lines += "Host: 127.0.0.1:$port" }
    foreach ($k in $h.Keys) { if ($null -ne $h[$k]) { $lines += "${k}: $($h[$k])" } }
    if ($null -ne $body) { $lines += "Content-Length: $([Text.Encoding]::UTF8.GetByteCount($body))" }
    return (($lines -join "`r`n") + "`r`n`r`n" + $body)
}
# Waits for responses while pumping this (UI) thread: /api/focus runs the desktop's focus code on it.
function Pump($tasks) { while (@($tasks | Where-Object { -not $_.IsCompleted }).Count) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 5 } }
function Req([int]$port, [string]$method, [string]$path, [hashtable]$h = @{}, [string]$body = $null, [switch]$Pump) {
    $text = RawReq $port $method $path $h $body
    if ($Pump) { $t = [THttp]::SendAsync($port, $text, 30000); Pump $t; $raw = $t.Result }
    else { $raw = [THttp]::Send($port, $text, 30000) }
    return @{ Code = [THttp]::Status($raw); Body = [THttp]::Body($raw); Raw = $raw }
}
function Api([int]$port) { (Req $port 'GET' '/api/board').Body | ConvertFrom-Json }
function DismissHeaders([int]$port) { @{ Origin = "http://127.0.0.1:$port"; 'Content-Type' = 'application/json'; 'X-Claude-Status' = 'dismiss'; 'Sec-Fetch-Site' = 'same-origin' } }
function Dis([int]$port, [string]$k, [string]$d, [hashtable]$override = @{}) {
    $h = DismissHeaders $port; foreach ($x in $override.Keys) { $h[$x] = $override[$x] }
    $r = Req $port 'POST' '/api/dismiss' $h ('{"k":"' + $k + '","d":"' + $d + '"}')
    $r.Result = try { ($r.Body | ConvertFrom-Json).result } catch { '' }
    return $r
}
function Row($board, [string]$name) { $board.rows | Where-Object { $_.n -eq $name } | Select-Object -First 1 }
function SessFile([string]$root, [string]$id) { Join-Path $root "state\sessions\$id.json" }

# --- off by default; turning it on/off from Settings starts/stops it ---
$pp = [THttp]::FreePort()
$root = New-Root @{ phonePort = $pp }
$f = Form $root
Check 'phone dashboard is off by default (nothing listening)' (-not $f.Phone -and -not [THttp]::CanConnect($pp) -and $f.PhoneStatus -eq 'Phone dashboard: off')
$f.SetSetting('phoneDashboard', $true)
Check 'Settings: turning it on starts it immediately' ($f.Phone -and $f.Phone.Running -and [THttp]::CanConnect($pp)) $f.PhoneStatus
$listen = @(Get-NetTCPConnection -State Listen -LocalPort $pp -ErrorAction SilentlyContinue)
Check 'listens on 127.0.0.1 only (not on the LAN or Tailscale interfaces)' ($listen.Count -ge 1 -and @($listen | Where-Object { $_.LocalAddress -ne '127.0.0.1' }).Count -eq 0) (($listen | ForEach-Object { $_.LocalAddress }) -join ',')
$f.SetSetting('phoneDashboard', $false)
Check 'Settings: turning it off stops it and frees the port' (-not $f.Phone -and -not [THttp]::CanConnect($pp))
$f.Dispose()
$changes = @{}
$sf = New-Object ClaudeStatus.SettingsForm('urgency', $false, 'workerBoard', $true, [Action[string, object]] { param($k, $v) $changes[$k] = $v }, [Action] { })
$null = $sf.Handle
$cb = @($sf.Controls[0].Controls | Where-Object { $_ -is [System.Windows.Forms.CheckBox] -and $_.Text -like 'Phone dashboard*' })
if ($cb.Count) { $cb[0].Checked = $false }
Check 'Settings window has a "Phone dashboard" checkbox that saves phoneDashboard' ($cb.Count -eq 1 -and $changes['phoneDashboard'] -eq $false)
$sf.Dispose()

# --- parity with the desktop board ---
$pp = [THttp]::FreePort()
$root = New-PhoneRoot $pp
Put $root 'ny' 'NEEDS_YOU' 'Toah_Nipi_Portal' 130 50 $PID $me
Put $root 'er' 'ERROR' 'Analytics' 200 40 $PID $me
Put $root 'b1' 'WORKING' 'Toah_Nipi_Booking_Software' 272 30 $PID $me
Put $root 'b2' 'WORKING' 'Toah_Nipi_Booking_Software' 68 20 $PID $me
Put $root 'web' 'DONE' 'Toah_Nipi_Website' 360 60 -updSec 360
Put $root 'gone' 'WORKING' 'Portal' 10 5 $dead.Id $deadStart          # crashed: the desktop prunes it
$j = Get-Content (SessFile $root 'er') -Raw | ConvertFrom-Json; $j.detail = 'authentication_failed'; [IO.File]::WriteAllText((SessFile $root 'er'), ($j | ConvertTo-Json -Compress))
$f = Form $root
$bd = Api $pp
$desk = ($f.Workers | ForEach-Object { $_.DisplayName + '=' + $_.State }) -join '|'
$ph = ($bd.rows | ForEach-Object { $_.n + '=' + $_.s }) -join '|'
Check 'phone rows = desktop rows: same names, #numbers, states and order' ($ph -eq $desk -and $ph -eq 'Portal=NEEDS_YOU|Analytics=ERROR|Booking Software #2=WORKING|Booking Software #1=WORKING|Website=DONE') "$ph / $desk"
Check 'desktop cleanup applies to the phone (crashed worker not shown)' (-not ($bd.rows | Where-Object { $_.n -eq 'Portal' -and $_.s -eq 'WORKING' }))
$sinceOk = $true
for ($i = 0; $i -lt $f.Workers.Count; $i++) { $w = $f.Workers[$i]; if ([long]$bd.rows[$i].t -ne [long][Math]::Floor(($w.StateSince - $epoch).TotalMilliseconds)) { $sinceOk = $false } }
Check 'timers: phone gets the same state start times as the desktop' $sinceOk
Check 'server clock included for skew-free timers' ([Math]::Abs([long]$bd.now - [long]([DateTime]::UtcNow - $epoch).TotalMilliseconds) -lt 5000)
Check 'error type is the desktop short form (authentication_failed -> auth)' ((Row $bd 'Analytics').e -eq 'auth')
Check 'only DONE rows carry a dismiss token' (((Row $bd 'Website').d -match '^[A-Za-z0-9_-]{22}$') -and @($bd.rows | Where-Object { $_.s -ne 'DONE' -and $_.d -ne '' }).Count -eq 0)
$rawJson = (Req $pp 'GET' '/api/board').Body
$fields = ($bd.rows | ForEach-Object { ($_.PSObject.Properties.Name) -join ',' } | Sort-Object -Unique) -join ';'
Check 'privacy: rows contain only k,n,s,t,e,d,c; board only v,now,summary,text,rows' ($fields -eq 'k,n,s,t,e,d,c' -and (($bd.PSObject.Properties.Name) -join ',') -eq 'v,now,summary,text,rows') "$fields"
Check 'privacy: no session IDs, folder names, process IDs or paths in the phone data' ($rawJson -notmatch '"(ny|er|b1|b2|web)"' -and $rawJson -notmatch 'Toah_Nipi|Users|claude_pid|\\\\' -and $rawJson -notmatch "\b$PID\b")
$f.SetSetting('workerSort', 'recent')
$bd = Api $pp
Check 'ordering setting (Most recent) applies to the phone too' ((($bd.rows | ForEach-Object { $_.n }) -join '|') -eq (($f.Workers | ForEach-Object { $_.DisplayName }) -join '|'))
$f.SetSetting('showWorkerSummary', $true)
$bd = Api $pp
Check 'summary setting applies to the phone too' ($bd.summary -eq $true -and $bd.text -eq $B::Summary($f.Workers))
$f.SetSetting('showWorkerSummary', $false); $f.SetSetting('workerSort', 'urgency')

# --- long-poll: changes arrive without a refresh ---
$v0 = (Api $pp).v
$t = [THttp]::SendAsync($pp, "GET /api/board?v=$v0&wait=1 HTTP/1.1`r`nHost: 127.0.0.1:$pp`r`n`r`n", 30000)
Start-Sleep -Milliseconds 400
$waiting = -not $t.IsCompleted
Put $root 'new' 'WORKING' 'Guest_Booking_Form' 1 0 $PID $me
$sw = [Diagnostics.Stopwatch]::StartNew(); $f.ReloadNow()
$done = $t.Wait(3000)
$nb = if ($done) { [THttp]::Body($t.Result) | ConvertFrom-Json } else { $null }
Check 'long-poll waits while nothing changes, answers as soon as the board changes' ($waiting -and $done -and $sw.ElapsedMilliseconds -lt 1500 -and $nb.v -gt $v0 -and (Row $nb 'Guest Form').s -eq 'WORKING') "waiting=$waiting done=$done $($sw.ElapsedMilliseconds)ms"
$f.ReloadNow(); $v1 = (Api $pp).v
Check 'unchanged board: no version bump (periodic reloads do not wake phones)' ($v1 -eq $nb.v)
$r = Req $pp 'GET' "/api/board?v=$v0&wait=1"
Check 'an out-of-date version gets the current board immediately' ($r.Code -eq 200 -and ($r.Body | ConvertFrom-Json).v -eq $v1)

# --- dismissal from the phone propagates to the desktop, and back ---
$bd = Api $pp; $web = Row $bd 'Website'
$r = Dis $pp $web.k $web.d
Check 'phone dismiss of a DONE row: dismissed, state file removed' ($r.Code -eq 200 -and $r.Result -eq 'dismissed' -and -not (Test-Path (SessFile $root 'web'))) "$($r.Code) $($r.Body)"
$f.ReloadNow()
Check 'phone dismiss removes the row from the desktop board' (-not ($f.Workers | Where-Object Id -eq 'web'))
Check '...and from the phone board' (-not (Row (Api $pp) 'Website'))
$r2 = Dis $pp $web.k $web.d
Check 'repeating the same dismiss is harmless ("gone", nothing else touched)' ($r2.Code -eq 200 -and $r2.Result -eq 'gone' -and @(Get-ChildItem (Join-Path $root 'state\sessions') -Filter *.json).Count -eq $f.Workers.Count)
Put $root 'web' 'WORKING' 'Toah_Nipi_Website' 1 0 $PID $me; $f.ReloadNow()
$bd = Api $pp
Check 'a later prompt in the dismissed session brings it back on both screens' ((Row $bd 'Website').s -eq 'WORKING' -and (Row $bd 'Website').k -eq $web.k -and ($f.Workers | Where-Object Id -eq 'web').State -eq 'WORKING')
Put $root 'web' 'DONE' 'Toah_Nipi_Website' 5 0; $f.ReloadNow()
$bd = Api $pp; $web2 = Row $bd 'Website'
Check 'each DONE occurrence gets a new dismiss token' ($web2.d -and $web2.d -ne $web.d)
Check 'desktop dismiss (x) of a DONE row' ($f.Dismiss('web'))
$f.ReloadNow()
Check 'desktop dismiss removes the row from the phone board' (-not (Row (Api $pp) 'Website'))
Check 'a phone dismiss after the desktop already dismissed it: harmless "gone"' ((Dis $pp $web2.k $web2.d).Result -eq 'gone')

# --- refusals ---
$bd = Api $pp
$r = Dis $pp (Row $bd 'Portal').k ('A' * 22)
Check 'NEEDS YOU cannot be dismissed (409, file kept)' ($r.Code -eq 409 -and $r.Result -eq 'not_done' -and (Test-Path (SessFile $root 'ny')))
$r = Dis $pp (Row $bd 'Analytics').k ('A' * 22)
Check 'ERROR cannot be dismissed' ($r.Code -eq 409 -and (Test-Path (SessFile $root 'er')))
$r = Dis $pp (Row $bd 'Booking Software #1').k ('A' * 22)
Check 'WORKING cannot be dismissed' ($r.Code -eq 409 -and (Test-Path (SessFile $root 'b1')))
$r = Dis $pp ('Z' * 22) ('Z' * 22)
Check 'unknown worker token: "gone", nothing touched' ($r.Code -eq 200 -and $r.Result -eq 'gone' -and @(Get-ChildItem (Join-Path $root 'state\sessions') -Filter *.json).Count -eq $f.Workers.Count)
$bad = @(
    @('not JSON', 'nope'), @('missing fields', '{}'), @('wrong types', '{"k":1,"d":2}'),
    @('path as worker token', '{"k":"..\\..\\config.json","d":"AAAAAAAAAAAAAAAAAAAAAA"}'),
    @('session id instead of token', '{"k":"b1","d":"x"}'), @('nested objects', '{"k":{"a":{"b":{"c":{"d":1}}}},"d":"x"}')
)
$allBad = $true; $info = @()
foreach ($c in $bad) { $r = Req $pp 'POST' '/api/dismiss' (DismissHeaders $pp) $c[1]; if ($r.Code -ne 400) { $allBad = $false; $info += "$($c[0])=$($r.Code)" } }
Check 'malformed / invalid dismiss requests: 400, no files touched' ($allBad -and (Test-Path (SessFile $root 'b1')) -and (Test-Path (Join-Path $root 'config.json'))) ($info -join ', ')
$r = Req $pp 'POST' '/api/dismiss' (DismissHeaders $pp) ('{"k":"' + ('A' * 2000) + '","d":"x"}')
Check 'oversized body refused (413)' ($r.Code -eq 413)

# --- request protection (Host / Origin / cross-site) ---
$k = (Row $bd 'Portal').k
$cases = @(
    @('Host evil.example (DNS rebinding)', 'GET', '/api/board', @{ Host = 'evil.example' }, 421),
    @('Host 127.0.0.1 on another port', 'GET', '/api/board', @{ Host = '127.0.0.1:1' }, 421),
    @('no Host header', 'GET', '/api/board', @{ Host = $null }, 421),
    @('page on localhost', 'GET', '/', @{ Host = "localhost:$pp" }, 200),
    @('dismiss without Origin', 'POST', '/api/dismiss', @{ Origin = $null }, 403),
    @('dismiss from another site (Origin)', 'POST', '/api/dismiss', @{ Origin = 'https://evil.example' }, 403),
    @('dismiss with Origin "null"', 'POST', '/api/dismiss', @{ Origin = 'null' }, 403),
    @('dismiss marked cross-site by the browser', 'POST', '/api/dismiss', @{ 'Sec-Fetch-Site' = 'cross-site' }, 403),
    @('dismiss as a plain form post (text/plain)', 'POST', '/api/dismiss', @{ 'Content-Type' = 'text/plain' }, 415),
    @('dismiss without the X-Claude-Status header', 'POST', '/api/dismiss', @{ 'X-Claude-Status' = $null }, 403),
    @('CORS preflight (OPTIONS)', 'OPTIONS', '/api/dismiss', @{}, 405),
    @('GET on the dismiss endpoint', 'GET', '/api/dismiss', @{}, 404),
    @('path traversal', 'GET', '/../config.json', @{}, 404),
    @('encoded traversal', 'GET', '/%2e%2e/config.json', @{}, 404),
    @('state file by name', 'GET', '/state/sessions/b1.json', @{}, 404),
    @('absolute-form request target', 'GET', 'http://evil.example/api/board', @{}, 400)
)
$protOk = $true; $info = @()
foreach ($c in $cases) {
    $h = if ($c[1] -eq 'POST') { DismissHeaders $pp } else { @{} }
    foreach ($x in $c[3].Keys) { $h[$x] = $c[3][$x] }
    $body = if ($c[1] -eq 'POST') { '{"k":"' + $k + '","d":"AAAAAAAAAAAAAAAAAAAAAA"}' } else { $null }
    $r = Req $pp $c[1] $c[2] $h $body
    if ($r.Code -ne $c[4]) { $protOk = $false; $info += "$($c[0]): $($r.Code) (want $($c[4]))" }
    if ($r.Raw -match '(?im)^Access-Control-Allow') { $protOk = $false; $info += "$($c[0]): CORS header" }
}
Check 'Host / Origin / cross-site / method / path checks' $protOk ($info -join '; ')
$r = Req $pp 'GET' '/'
Check 'page served with a strict CSP, no-store, nosniff, no framing, no CORS' ($r.Code -eq 200 -and $r.Raw -match "Content-Security-Policy: default-src 'none'" -and $r.Raw -match 'Cache-Control: no-store' -and $r.Raw -match 'X-Content-Type-Options: nosniff' -and $r.Raw -match 'X-Frame-Options: DENY' -and $r.Raw -notmatch 'Access-Control-Allow')
Check 'page, stylesheet and script served' ($r.Body -match 'CLAUDE WORKERS' -and (Req $pp 'GET' '/phone.css').Code -eq 200 -and (Req $pp 'GET' '/phone.js').Code -eq 200)

# --- creature looks: one per worker, stable while it's on the board ---
$bd = Api $pp
$looksOk = @($bd.rows | Where-Object { $_.c -isnot [int] -or $_.c -lt 0 -or $_.c -ge [ClaudeStatus.PhoneServer]::Looks }).Count -eq 0
$hats = @($bd.rows | ForEach-Object { $_.c % [ClaudeStatus.PhoneServer]::Hats })
Check 'each worker gets a creature look; no two workers on the board wear the same hat' ($looksOk -and ($hats | Sort-Object -Unique).Count -eq $hats.Count) (($bd.rows | ForEach-Object { "$($_.n)=$($_.c)" }) -join ', ')
$before = @{}; foreach ($w in $bd.rows) { $before[$w.k] = $w.c }
Put $root 'b1' 'NEEDS_YOU' 'Toah_Nipi_Booking_Software' 1 30 $PID $me; Put $root 'zz' 'WORKING' 'Another_One' 1 1 $PID $me; $f.ReloadNow()
$bd = Api $pp
Check 'looks stay the same when states change and workers come and go' (@($bd.rows | Where-Object { $before.ContainsKey($_.k) -and $before[$_.k] -ne $_.c }).Count -eq 0 -and @($bd.rows | Where-Object { $before.ContainsKey($_.k) }).Count -eq $before.Count)

# --- tap a creature: the desktop's own click action, with window lookup/activation mocked ---
$null = $f.Handle
$script:focusLog = New-Object System.Collections.ArrayList
$script:focusAnswer = @{}
$script:focusDelay = 0
$f.FocusHook = [Func[ClaudeStatus.SessionInfo, ClaudeStatus.FocusResult]] {
    param($si)
    [void]$script:focusLog.Add($si.Id)
    if ($script:focusDelay) { Start-Sleep -Milliseconds $script:focusDelay }
    if ($script:focusAnswer.ContainsKey($si.Id)) { $script:focusAnswer[$si.Id] } else { [ClaudeStatus.FocusResult]::Focused }
}
function FocusHeaders([int]$port) { $h = DismissHeaders $port; $h['X-Claude-Status'] = 'focus'; return $h }
function Foc([int]$port, [string]$k, [hashtable]$override = @{}, [string]$body = '') {
    $h = FocusHeaders $port; foreach ($x in $override.Keys) { $h[$x] = $override[$x] }
    if (-not $body) { $body = '{"k":"' + $k + '"}' }
    $r = Req $port 'POST' '/api/focus' $h $body -Pump
    $r.Result = try { ($r.Body | ConvertFrom-Json).result } catch { '' }
    return $r
}
$bd = Api $pp
$r = Foc $pp (Row $bd 'Booking Software #2').k
Check 'creature tap reaches the desktop focus routine for exactly that worker (#2, not #1 of the same project)' ($r.Code -eq 200 -and $r.Result -eq 'focused' -and ($script:focusLog -join ',') -eq 'b2') "$($r.Code) $($r.Body) log=$($script:focusLog -join ',')"
# the desktop click on a worker's text goes through the same routine
$bf = [Reflection.BindingFlags]'NonPublic,Instance'
$i = [array]::IndexOf(@($f.Workers | ForEach-Object { $_.Id }), 'b1')
$rb = $f.RowBounds($i); $pt = New-Object Drawing.Point(($rb.X + 30), ($rb.Y + [int]($rb.Height / 2)))
$hit = $f.HitAt($pt)
$mea = New-Object Windows.Forms.MouseEventArgs([Windows.Forms.MouseButtons]::Left, 1, $pt.X, $pt.Y, 0)
$margs = [object[]]@($mea.psobject.BaseObject)
$null = $f.GetType().GetMethod('OnMouseDown', $bf).Invoke($f, $margs); $null = $f.GetType().GetMethod('OnMouseUp', $bf).Invoke($f, $margs)
Check 'a desktop click on a worker uses that same routine' ($hit -and $hit -notlike 'x:*' -and $script:focusLog[-1] -eq 'b1') "hit=$hit log=$($script:focusLog -join ',')"

# rapid repeated taps: one activation
$script:focusLog.Clear(); $script:focusDelay = 300
$k = (Row $bd 'Portal').k
$text = RawReq $pp 'POST' '/api/focus' (FocusHeaders $pp) ('{"k":"' + $k + '"}')
$tasks = @(1..3 | ForEach-Object { [THttp]::SendAsync($pp, $text, 30000) }); Pump $tasks
$res = @($tasks | ForEach-Object { ([THttp]::Body($_.Result) | ConvertFrom-Json).result })
$r = Foc $pp $k
$script:focusDelay = 0
Check 'rapid repeated taps (3 at once, then 1 more): one activation; the others get "busy" or the same answer' ($script:focusLog.Count -eq 1 -and @($res | Where-Object { $_ -eq 'focused' }).Count -ge 1 -and @($res | Where-Object { $_ -notin 'focused', 'busy' }).Count -eq 0 -and $r.Result -eq 'focused') "calls=$($script:focusLog.Count) results=$($res -join ',') then $($r.Result)"

# activation and dismissal never trigger each other
Put $root 'dn' 'DONE' 'Done_Project' 30 2; $f.ReloadNow()
$dn = Row (Api $pp) 'Done Project'
$script:focusLog.Clear()
$r = Foc $pp $dn.k
Check 'tapping a DONE creature brings it up and does not dismiss it' ($r.Result -eq 'focused' -and ($script:focusLog -join ',') -eq 'dn' -and (Test-Path (SessFile $root 'dn')) -and (Row (Api $pp) 'Done Project'))
$r = Dis $pp $dn.k $dn.d
Check 'its x dismisses it and does not bring anything up' ($r.Result -eq 'dismissed' -and $script:focusLog.Count -eq 1)
$f.ReloadNow()
Put $root 'dn' 'WORKING' 'Done_Project' 1 2 $PID $me; $f.ReloadNow()
Check 'a dismissed worker that comes back keeps its creature' ((Row (Api $pp) 'Done Project').c -eq $dn.c)

# answers come from the desktop routine; stale or unknown references are refused without acting
$script:focusAnswer['er'] = [ClaudeStatus.FocusResult]::Locked
$script:focusAnswer['ny'] = [ClaudeStatus.FocusResult]::Ended
$script:focusAnswer['b1'] = [ClaudeStatus.FocusResult]::Refused
$bd = Api $pp; $script:focusLog.Clear()
$a1 = Foc $pp (Row $bd 'Analytics').k; $a2 = Foc $pp (Row $bd 'Portal').k; $a3 = Foc $pp (Row $bd 'Booking Software #1').k
Check 'outcomes reported as the laptop saw them: locked / ended / refused (409), never "focused"' ($a1.Code -eq 409 -and $a1.Result -eq 'locked' -and $a2.Result -eq 'ended' -and $a3.Result -eq 'refused') "$($a1.Result) $($a2.Result) $($a3.Result)"
$script:focusLog.Clear()
Remove-Item (SessFile $root 'zz'); $old = Row $bd 'Another One'; $f.ReloadNow()
$r1 = Foc $pp $old.k; $r2 = Foc $pp ('Z' * 22)
Check 'a worker that has left the board, or an unknown token: "gone", nothing activated' ($r1.Code -eq 409 -and $r1.Result -eq 'gone' -and $r2.Result -eq 'gone' -and $script:focusLog.Count -eq 0) "$($r1.Result) $($r2.Result) calls=$($script:focusLog.Count)"
$allBad = $true; $info = @()
foreach ($c in @('nope', '{}', '{"k":1}', '{"k":"..\\..\\config.json"}', '{"k":"b1"}', '{"path":"C:\\Windows\\notepad.exe"}', '{"k":{"a":{"b":{"c":1}}}}')) {
    $r = Foc $pp '' @{} $c; if ($r.Code -ne 400) { $allBad = $false; $info += "$c=$($r.Code)" }
}
Check 'malformed focus requests (paths, session ids, wrong types, no token): 400, nothing activated' ($allBad -and $script:focusLog.Count -eq 0) ($info -join ', ')
$k = (Row $bd 'Booking Software #2').k
$r = Foc $pp $k @{} ('{"k":"' + $k + '","path":"C:\\Windows\\notepad.exe","exe":"calc.exe","pid":4,"args":"/c dir","url":"http://evil.example"}')
Check 'extra fields (path, exe, pid, args, url) are ignored: only the token picks the worker, the laptop picks the window' ($r.Result -eq 'focused' -and ($script:focusLog -join ',') -eq 'b2')
$script:focusLog.Clear()
$cases = @(
    @('focus without Origin', @{ Origin = $null }, 403), @('focus from another site', @{ Origin = 'https://evil.example' }, 403),
    @('focus marked cross-site', @{ 'Sec-Fetch-Site' = 'cross-site' }, 403), @('focus without X-Claude-Status', @{ 'X-Claude-Status' = $null }, 403),
    @('focus with the dismiss marker', @{ 'X-Claude-Status' = 'dismiss' }, 403), @('focus as a form post', @{ 'Content-Type' = 'application/x-www-form-urlencoded' }, 415),
    @('focus with a rebinding Host', @{ Host = 'evil.example' }, 421)
)
$protOk = $true; $info = @()
foreach ($c in $cases) { $r = Foc $pp $k $c[1]; if ($r.Code -ne $c[2] -or $r.Raw -match '(?im)^Access-Control-Allow') { $protOk = $false; $info += "$($c[0]): $($r.Code)" } }
foreach ($m in 'GET', 'OPTIONS', 'PUT') { $r = Req $pp $m '/api/focus' -Pump; if ($r.Code -ne $(if ($m -eq 'GET') { 404 } else { 405 })) { $protOk = $false; $info += "$m=$($r.Code)" } }
$h2 = DismissHeaders $pp; $h2['X-Claude-Status'] = 'focus'
if ((Req $pp 'POST' '/api/dismiss' $h2 ('{"k":"' + $k + '","d":"' + ('A' * 22) + '"}')).Code -ne 403) { $protOk = $false; $info += 'dismiss with the focus marker accepted' }
Check 'focus endpoint: Origin / cross-site / header / JSON / Host / method protections, no CORS; nothing activated' ($protOk -and $script:focusLog.Count -eq 0) ($info -join '; ')
$f.FocusHook = $null
# The real path (no mock), for workers whose window can't be resolved, so nothing is ever activated.
Put $root 'np' 'WORKING' 'No_Identity' 1 1
Put $root 'dp' 'DONE' 'Ended_Session' 5 1 $dead.Id $deadStart
$f.ReloadNow(); $bd = Api $pp
$r1 = Foc $pp (Row $bd 'No Identity').k; $r2 = Foc $pp (Row $bd 'Ended Session').k
Check 'unmocked phone tap runs the real window lookup: no process identity -> "not_yet", ended session -> "ended" (nothing focused)' ($r1.Result -eq 'not_yet' -and $r2.Result -eq 'ended') "$($r1.Result) $($r2.Result)"
Info "lock-screen check in this session: InputDesktopLocked = $([ClaudeStatus.WorkerWindows]::InputDesktopLocked())"
$f.Dispose()

# Tailnet names (as forwarded by tailscale serve), with a standalone server and fixed names
$pp2 = [THttp]::FreePort()
$srvRoot = New-PhoneRoot $pp2
$srv = New-Object ClaudeStatus.PhoneServer($srvRoot, $pp2, (Dismisser (Join-Path $srvRoot 'state\sessions')), [TFake]::Names(@('laptop.tail1234.ts.net', 'laptop', '100.64.0.7')))
$null = $srv.Start()
$srv.Publish((New-Object 'System.Collections.Generic.List[ClaudeStatus.SessionInfo]'), $false, '')
$ok = $true; $info = @()
foreach ($hh in 'laptop.tail1234.ts.net', 'LAPTOP.tail1234.ts.net.', 'laptop', 'laptop:80', '100.64.0.7', 'laptop.tail1234.ts.net:443') { $c = (Req $pp2 'GET' '/api/board' @{ Host = $hh }).Code; if ($c -ne 200) { $ok = $false; $info += "$hh=$c" } }
foreach ($hh in 'other.tail1234.ts.net', 'laptop.evil.example', 'laptop.tail1234.ts.net.evil.example', '100.64.0.8') { $c = (Req $pp2 'GET' '/api/board' @{ Host = $hh }).Code; if ($c -ne 421) { $ok = $false; $info += "$hh=$c" } }
Check "this machine's Tailscale names/IPs accepted; look-alikes refused" $ok ($info -join ', ')
$o1 = (Req $pp2 'POST' '/api/dismiss' @{ Host = 'laptop.tail1234.ts.net'; Origin = 'http://laptop.tail1234.ts.net'; 'Content-Type' = 'application/json'; 'X-Claude-Status' = 'dismiss' } ('{"k":"' + ('A' * 22) + '","d":"' + ('A' * 22) + '"}')).Code
$o2 = (Req $pp2 'POST' '/api/dismiss' @{ Host = 'laptop.tail1234.ts.net'; Origin = 'http://laptop.tail1234.ts.net.evil.example'; 'Content-Type' = 'application/json'; 'X-Claude-Status' = 'dismiss' } ('{"k":"' + ('A' * 22) + '","d":"' + ('A' * 22) + '"}')).Code
Check 'dismiss via the Tailscale name allowed; a look-alike Origin refused' ($o1 -eq 200 -and $o2 -eq 403) "$o1 / $o2"
$srv2 = New-Object ClaudeStatus.PhoneServer($srvRoot, $pp2, (Dismisser (Join-Path $srvRoot 'state\sessions')), [TFake]::Names(@()))
Check 'a second server on the same port fails cleanly (no duplicate instances)' (-not $srv2.Start() -and $srv2.Error -match 'already in use') $srv2.Error
$t = [THttp]::SendAsync($pp2, "GET /api/board?v=1&wait=1 HTTP/1.1`r`nHost: 127.0.0.1:$pp2`r`n`r`n", 30000)
Start-Sleep -Milliseconds 300
$srv.Stop()
Check 'stopping releases waiting phones at once and frees the port' ($t.Wait(2000) -and -not [THttp]::CanConnect($pp2))

# --- the delayed-dismissal race and concurrent hook writes ---
$pp = [THttp]::FreePort()
$root = New-PhoneRoot $pp
Put $root 'race' 'DONE' 'Toah_Nipi_Website' 30 3
$f = Form $root
$seen = Row (Api $pp) 'Website'                  # 1. the phone shows it as DONE
Put $root 'race' 'WORKING' 'Toah_Nipi_Website' 0 3 $PID $me   # 2. a new prompt arrives (the board hasn't reloaded yet)
$r = Dis $pp $seen.k $seen.d                       # 3. the delayed dismiss arrives
Check 'delayed dismiss after a new prompt: refused, the WORKING worker stays' ($r.Code -eq 409 -and (Get-Content (SessFile $root 'race') -Raw | ConvertFrom-Json).state -eq 'WORKING') "$($r.Code) $($r.Body)"
$f.ReloadNow()
Check '...and it is still on both boards' ((Row (Api $pp) 'Website').s -eq 'WORKING' -and ($f.Workers | Where-Object Id -eq 'race').State -eq 'WORKING')
Put $root 'race' 'DONE' 'Toah_Nipi_Website' 0 3; $f.ReloadNow()
$r = Dis $pp $seen.k $seen.d
Check 'a dismiss for an earlier DONE does not remove a newer DONE of the same session' ($r.Code -eq 409 -and (Test-Path (SessFile $root 'race')))
$f.Dispose()

$raceDir = Join-Path $tmp 'race'; $null = New-Item -ItemType Directory -Force $raceDir
$rf = Join-Path $raceDir 'r.json'
$workJson = '{"v":2,"session_id":"r","state":"WORKING","event":"UserPromptSubmit","detail":"","project":"P","updated":"2026-10-28T10:00:05.0000000Z","state_since":"2026-10-28T10:00:05.0000000Z","first_seen":"2026-10-28T09:00:00.0000000Z","claude_pid":0,"claude_started":""}'
$doneJson = '{"v":2,"session_id":"r","state":"DONE","event":"Stop","detail":"","project":"P","updated":"2026-10-28T10:00:00.0000000Z","state_since":"2026-10-28T10:00:00.0000000Z","first_seen":"2026-10-28T09:00:00.0000000Z","claude_pid":0,"claude_started":""}'
$si = $B::Parse('r', $ser.Deserialize($doneJson, [System.Collections.Generic.Dictionary[string, object]]), [DateTime]::MinValue, [DateTime]::MinValue)
$occ = [ClaudeStatus.Dismissal]::Occurrence($si)
$res = [TRace]::Run($rf, 'r', $doneJson, $workJson, $occ, (Dismisser $raceDir), 400, 7)
$outcomes = @{}; foreach ($kv in $res.Split(';')) { $p2 = $kv.Split('='); $outcomes[$p2[0]] = [int]$p2[1] }
Check 'concurrent hook write vs dismiss (400 random interleavings): the new WORKING state is never deleted' ($outcomes['lost'] -eq 0) $res
Info "race outcomes: $res"
Check 'both orders actually occurred in the race test' ($outcomes['Dismissed'] -gt 0 -and ($outcomes['NotDone'] -gt 0 -or $outcomes['Changed'] -gt 0)) $res
Check 'dismissal refuses ids that could be paths' ([ClaudeStatus.Dismissal]::DismissDone($raceDir, '..\race\r', $occ) -eq 'Gone' -and [ClaudeStatus.Dismissal]::DismissDone($raceDir, 'r.json', $occ) -eq 'Gone' -and (Test-Path $rf))

# --- lifecycle: the desktop never depends on the phone side ---
$busy = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0); $busy.Start()
$bp = $busy.LocalEndpoint.Port
$root = New-PhoneRoot $bp
Put $root 'w' 'WORKING' 'Toah_Nipi_Portal' 5 1 $PID $me
Put $root 'd' 'DONE' 'Toah_Nipi_Website' 5 2
$f = Form $root
Check 'port already in use: phone side reports it, desktop board works normally' (-not $f.Phone -and $f.PhoneStatus -match 'already in use' -and $f.Workers.Count -eq 2 -and $f.Dismiss('d')) $f.PhoneStatus
$f.GetType().GetMethod('BuildMenu', [Reflection.BindingFlags]'NonPublic,Instance').Invoke($f, @())
$menu = $f.GetType().GetField('menu', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($f)
Check 'right-click menu shows the phone dashboard problem' (@($menu.Items | Where-Object { $_.Text -match '^Phone dashboard: not running: port \d+ is already in use$' }).Count -eq 1)
$np = [THttp]::FreePort(); $f.SetSetting('phonePort', $np)
Check 'changing phonePort starts it on the new port right away' ($f.Phone -and [THttp]::CanConnect($np))
# A child process started with inherited handles (as the tailscale.exe lookup is) must not keep the port alive.
$child = Start-Process $winPs -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 6' -RedirectStandardOutput (Join-Path $tmp 'child.txt') -WindowStyle Hidden -PassThru
Start-Sleep -Milliseconds 300
$f.SetSetting('phoneDashboard', $false)
Check 'switching off frees the port even while a child process that inherited handles is running' (-not $child.HasExited -and -not [THttp]::CanConnect($np))
$f.SetSetting('phoneDashboard', $true)
$child.Kill()
$busy.Stop()
$f.Dispose()
Check 'closing the indicator stops the phone server (no orphan listener)' (-not [THttp]::CanConnect($np))
$root = New-Root @{ phoneDashboard = $true; phonePort = ([THttp]::FreePort()) }   # page files missing
$f = Form $root
$r = Req $f.Phone.Port 'GET' '/'
Check 'missing page files: clear error, the board API still works' ($r.Code -eq 500 -and $r.Body -match 'install' -and (Req $f.Phone.Port 'GET' '/api/board').Code -eq 200)
$f.Dispose()

# --- phone.js timers = indicator.cs timers ---
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) { Skip 'phone timer text matches the desktop' 'node not found (only needed for this check)' }
else {
    $secs = @(0, 0.5, 1, 59, 59.9, 60, 61, 119, 125, 272, 599, 3599, 3599.9, 3600, 3601, 3660, 3661, 3900, 7300, 43200, 86399, 86400, 90061, 183600, 259205, 950400)
    $errs = @('', 'rate_limit', 'authentication_failed', 'billing_error', 'server_error', 'max_output_tokens', 'a_really_long_error_identifier')
    $cases = @(); $want = @()
    foreach ($sec in $secs) {
        $ms = [long]($sec * 1000); $ts = [TimeSpan]::FromMilliseconds($ms)
        foreach ($st in 'WORKING', 'NEEDS_YOU', 'DONE', 'ERROR') {
            foreach ($e in $(if ($st -eq 'ERROR') { $errs } else { @('') })) {
                foreach ($brief in $false, $true) {
                    $s0 = New-Object ClaudeStatus.SessionInfo; $s0.State = $st; $s0.Detail = $e; $s0.StateSince = $now.AddMilliseconds(-$ms)
                    $cases += , @($st, $B::ShortError($e), $ms, $brief)
                    $want += $B::StatusText($s0, $now, $brief)
                }
            }
        }
        $cases += , @('clock', '', $ms, $false); $want += $B::Clock($ts)
        $cases += , @('span', '', $ms, $false); $want += $B::Span($ts)
        $cases += , @('ago', '', $ms, $false); $want += $B::Ago($ts)
    }
    $inFile = Join-Path $tmp 'timer-cases.json'
    [IO.File]::WriteAllText($inFile, ($cases | ConvertTo-Json -Compress -Depth 3), (New-Object Text.UTF8Encoding $false))
    # output is \u-escaped ASCII so the console code page can't mangle "·" / "…"
    $js = @'
const f=require(process.argv[1]);const c=JSON.parse(require('fs').readFileSync(process.argv[2],'utf8'));
const out=c.map(x=>x[0]==='clock'?f.clock(x[2]):x[0]==='span'?f.span(x[2]):x[0]==='ago'?f.ago(x[2]):f.statusText(x[0],x[1],x[2],x[3]));
process.stdout.write(JSON.stringify(out).replace(/[\u007f-￿]/g,ch=>'\\u'+ch.charCodeAt(0).toString(16).padStart(4,'0')));
'@ -replace "`r?`n", ''
    $got = & $node.Source -e $js (Join-Path $src 'phone.js') $inFile | Out-String | ConvertFrom-Json
    $diff = @(); for ($i = 0; $i -lt $want.Count; $i++) { if ($got[$i] -cne $want[$i]) { $diff += "$(($cases[$i]) -join '/'): js='$($got[$i])' cs='$($want[$i])'" } }
    Check "phone timer/status text matches the desktop exactly ($($want.Count) cases)" ($got.Count -eq $want.Count -and $diff.Count -eq 0) (($diff | Select-Object -First 5) -join '; ')
    $art = @'
const f=require(process.argv[1]);const bad=[];
f.HATS.forEach(h=>{h.r.forEach(r=>{if(r.length!==16)bad.push(h.n+' row width '+r.length);for(const ch of r)if(ch!=='.'&&!h.c[ch])bad.push(h.n+' colour '+ch);});});
for(let l=0;l<f.HATS.length*f.BODIES.length;l++){const walk=a=>a.forEach(x=>Array.isArray(x[1][0])&&typeof x[1][0][0]==='string'?walk(x[1]):x[1].forEach(p=>{if(p[0]<0||p[0]>15||p[1]<0||p[1]>15||!/^#[0-9a-f]{6}$/.test(p[2]))bad.push('look '+l+' '+x[0]+' '+p);}));walk(f.creatureLayers(l));}
process.stdout.write(JSON.stringify({hats:f.HATS.length,bodies:f.BODIES.length,bad:bad.slice(0,5)}));
'@ -replace "`r?`n", ''
    $a = & $node.Source -e $art (Join-Path $src 'phone.js') | ConvertFrom-Json
    Check 'creature art: 13 hats x 5 bodies (= the laptop''s look numbers), every square inside the 16x16 grid with a colour' ($a.hats -eq [ClaudeStatus.PhoneServer]::Hats -and $a.hats * $a.bodies -eq [ClaudeStatus.PhoneServer]::Looks -and @($a.bad).Count -eq 0) (@($a.bad) -join '; ')
}

# ======================================================================================
Section 'Phone page in a real browser (headless Chrome emulating Android phones)'
# ======================================================================================
$chrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$nodeCmd = Get-Command node -ErrorAction SilentlyContinue
if (-not $Browser) { Skip 'phone page in a browser' 'run with -Browser' }
elseif (-not $chrome -or -not $nodeCmd) { Skip 'phone page in a browser' 'needs Chrome or Edge, and Node.js 22+' }
else {
    $pp = [THttp]::FreePort()
    $root = New-PhoneRoot $pp
    Put $root 'ny' 'NEEDS_YOU' 'Toah_Nipi_Portal' 130 50 $PID $me
    Put $root 'er' 'ERROR' 'A_Very_Long_Project_Folder_Name_That_Keeps_Going_And_Going' 200 45 $PID $me
    $j = Get-Content (SessFile $root 'er') -Raw | ConvertFrom-Json; $j.detail = 'rate_limit'; [IO.File]::WriteAllText((SessFile $root 'er'), ($j | ConvertTo-Json -Compress))
    Put $root 'er2' 'ERROR' 'Analytics' 400 44 $PID $me
    $j = Get-Content (SessFile $root 'er2') -Raw | ConvertFrom-Json; $j.detail = 'authentication_failed'; [IO.File]::WriteAllText((SessFile $root 'er2'), ($j | ConvertTo-Json -Compress))
    Put $root 'b1' 'WORKING' 'Toah_Nipi_Booking_Software' 272 30 $PID $me
    Put $root 'b2' 'WORKING' 'Toah_Nipi_Booking_Software' 68 20 $PID $me
    Put $root 'b3' 'WORKING' 'Toah_Nipi_Booking_Software' 4000 10 $PID $me
    foreach ($i in 1..6) { Put $root "x$i" 'WORKING' "Side_Project_$i" (30 * $i) (60 + $i) $PID $me }
    Put $root 'web' 'DONE' 'Toah_Nipi_Website' 30 60
    Put $root 'guest' 'DONE' 'Guest_Booking_Form' 360 59
    Put $root 'race' 'DONE' 'Race' 7300 58
    $f = Form $root
    $cdir = Join-Path $tmp 'browser'; $null = New-Item -ItemType Directory -Force $cdir
    $profileDir = Join-Path $tmp 'chrome-profile'
    $cp = Start-Process $chrome -ArgumentList '--headless=new', '--remote-debugging-port=0', "--user-data-dir=`"$profileDir`"", '--no-first-run', '--no-default-browser-check', '--disable-extensions', 'about:blank' -PassThru
    $portFile = Join-Path $profileDir 'DevToolsActivePort'
    for ($i = 0; $i -lt 100 -and -not (Test-Path $portFile); $i++) { Start-Sleep -Milliseconds 100 }
    $dbg = if (Test-Path $portFile) { (Get-Content $portFile)[0] } else { '' }
    if (-not $dbg) { Check 'headless browser started' $false }
    else {
        $np = Start-Process $nodeCmd.Source -ArgumentList "`"$(Join-Path $PSScriptRoot 'phone-browser.mjs')`"", $dbg, $pp, "`"$cdir`"", "`"$RenderDir`"" -PassThru -NoNewWindow -RedirectStandardError (Join-Path $cdir 'stderr.txt')
        $handled = @{}
        # Creature taps reach the desktop's focus routine with window lookup/activation mocked: it records
        # which worker would be brought up (and answers "locked" for one, to check the message).
        $null = $f.Handle
        $script:browserFocus = New-Object System.Collections.ArrayList
        $f.FocusHook = [Func[ClaudeStatus.SessionInfo, ClaudeStatus.FocusResult]] {
            param($si)
            [void]$script:browserFocus.Add($si.Id)
            Start-Sleep -Milliseconds 400
            if ($si.Id -eq 'pe2') { [ClaudeStatus.FocusResult]::Locked } else { [ClaudeStatus.FocusResult]::Focused }
        }
        $deadline = (Get-Date).AddMinutes(6)
        while (-not $np.HasExited -and (Get-Date) -lt $deadline) {
            [System.Windows.Forms.Application]::DoEvents()
            foreach ($req in @(Get-ChildItem $cdir -Filter 'req-*' -ErrorAction SilentlyContinue)) {
                $name = $req.Name.Substring(4)
                if ($handled[$name]) { continue }
                $handled[$name] = $true
                switch -Wildcard ($name) {
                    'desktop-dismiss'     { $null = $f.Dismiss('web') }
                    'check-phone-dismiss' { Start-Sleep -Milliseconds 200; $script:phoneDismissedFile = -not (Test-Path (SessFile $root 'guest')); $f.ReloadNow() }
                    'hidden-change'       { Put $root 'b2' 'NEEDS_YOU' 'Toah_Nipi_Booking_Software' 1 20 $PID $me; $f.ReloadNow() }
                    'race-prompt'         { Put $root 'race' 'WORKING' 'Race' 0 58 $PID $me }   # no reload yet: the phone still shows DONE
                    'reload'              { $f.ReloadNow() }
                    'stop'                { $f.SetSetting('phoneDashboard', $false) }
                    'start'               { $f.SetSetting('phoneDashboard', $true) }
                    'clear*'              { Get-ChildItem (Join-Path $root 'state\sessions') -Filter *.json | Remove-Item; $f.ReloadNow() }
                    'repopulate'          {
                        Put $root 'pny' 'NEEDS_YOU' 'Toah_Nipi_Portal' 130 50 $PID $me
                        Put $root 'pe1' 'ERROR' 'A_Very_Long_Project_Folder_Name_That_Keeps_Going_And_Going' 200 45 $PID $me
                        $j = Get-Content (SessFile $root 'pe1') -Raw | ConvertFrom-Json; $j.detail = 'rate_limit'; [IO.File]::WriteAllText((SessFile $root 'pe1'), ($j | ConvertTo-Json -Compress))
                        Put $root 'pe2' 'ERROR' 'Analytics' 400 44 $PID $me
                        Put $root 'pb1' 'WORKING' 'Toah_Nipi_Booking_Software' 272 30 $PID $me
                        Put $root 'pb2' 'WORKING' 'Toah_Nipi_Booking_Software' 68 20 $PID $me
                        foreach ($i in 1..3) { Put $root "px$i" 'WORKING' "Side_Project_$i" (30 * $i) (60 + $i) $PID $me }
                        Put $root 'pd1' 'DONE' 'Toah_Nipi_Website' 30 60
                        Put $root 'pd2' 'DONE' 'Guest_Booking_Form' 360 59
                        $f.ReloadNow()
                    }
                    'pets-change'         { Put $root 'pb1' 'NEEDS_YOU' 'Toah_Nipi_Booking_Software' 1 30 $PID $me; Put $root 'px1' 'DONE' 'Side_Project_1' 0 61; $f.ReloadNow() }
                    'focus-log*'          { [IO.File]::WriteAllText((Join-Path $cdir 'focus-log.txt'), ($script:browserFocus -join ',')) }
                    'focus-clear*'        { $script:browserFocus.Clear() }
                    'check-pet-dismiss'   { Start-Sleep -Milliseconds 200; $script:petDismissedFile = -not (Test-Path (SessFile $root 'pd1')) -and (Test-Path (SessFile $root 'pd2')); $f.ReloadNow() }
                }
                [IO.File]::WriteAllText((Join-Path $cdir "ack-$name"), '')
            }
            Start-Sleep -Milliseconds 50
        }
        if (-not $np.HasExited) { Stop-Process -Id $np.Id -Force; Check 'browser test finished in time' $false }
        $resFile = Join-Path $cdir 'results.txt'
        if (-not (Test-Path $resFile)) { Check 'browser test produced results' $false (Get-Content (Join-Path $cdir 'stderr.txt') -Raw -ErrorAction SilentlyContinue) }
        else {
            foreach ($line in Get-Content $resFile -Encoding UTF8) {
                $k = $line.Split("`t")
                switch ($k[0]) { 'PASS' { Check "browser: $($k[1])" $true } 'FAIL' { Check "browser: $($k[1])" $false $k[2] } 'INFO' { Info "browser: $($k[1])" } }
            }
        }
        Check 'browser: the tapped dismissal removed exactly that DONE state file' ($script:phoneDismissedFile -eq $true)
        Check 'browser: the stale race tap left the WORKING session file in place' ($handled['clear'] -or (Test-Path (SessFile $root 'race')))
        Check 'browser: the x on a creature card removed exactly that DONE state file' ($script:petDismissedFile -eq $true)
        Info "phone screenshots saved to $RenderDir (phone-*.png)"
    }
    & taskkill.exe /PID $cp.Id /T /F 2>&1 | Out-Null
    $f.Dispose()
}

# ======================================================================================
Section 'Live windows (live-tests.ps1, run hidden like the real indicator)'
# ======================================================================================
if (-not $Live) { Skip 'live window tests' 'run with -Live' }
else {
    $liveResults = Join-Path $tmp 'live-results.txt'
    $p = Start-Process $winPs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $PSScriptRoot 'live-tests.ps1')`"", '-ResultFile', "`"$liveResults`"", '-Tmp', "`"$tmp`"" -WindowStyle Hidden -PassThru -Wait
    if (-not (Test-Path $liveResults)) { Check 'live tests ran' $false "exit code $($p.ExitCode)" }
    else {
        foreach ($line in Get-Content $liveResults) {
            $k = $line.Split("`t")
            switch ($k[0]) { 'PASS' { Check $k[1] $true } 'FAIL' { Check $k[1] $false $k[2] } 'SKIP' { Skip $k[1] $k[2] } 'INFO' { Info $k[1] } 'SECTION' { Section $k[1] } }
        }
    }
}

# ======================================================================================
Section 'Installer: fresh install, update from the previous version, idempotency, uninstall'
# ======================================================================================
$cd = Join-Path $tmp 'claude'; $null = New-Item -ItemType Directory -Force $cd
$settingsPath = Join-Path $cd 'settings.json'
[IO.File]::WriteAllText($settingsPath, '{"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo my-own-hook"}]}]}}')
$inst = Join-Path $cd 'status-indicator'

# The previous version, straight from git
$oldRepo = Join-Path $tmp 'old-repo'; $null = New-Item -ItemType Directory -Force (Join-Path $oldRepo 'src')
$haveGit = [bool](Get-Command git -ErrorAction SilentlyContinue)
if ($haveGit) {
    foreach ($fn in @('install.ps1') + @(git -C $repo ls-tree --name-only HEAD src/)) {
        $content = git -C $repo show "HEAD:$fn" | Out-String
        [IO.File]::WriteAllText((Join-Path $oldRepo $fn), $content)
    }
    $out = & $winPs -NoProfile -ExecutionPolicy Bypass -File (Join-Path $oldRepo 'install.ps1') -ClaudeDir $cd -NoStart 2>&1
    Check 'previous version installs' ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $inst 'hook.ps1')))
    $c = Get-Content (Join-Path $inst 'config.json') -Raw | ConvertFrom-Json; $c.soundOnDone = $false; $c.staleHours = 12
    [IO.File]::WriteAllText((Join-Path $inst 'config.json'), ($c | ConvertTo-Json))
    [IO.File]::WriteAllText((Join-Path $inst 'position.txt'), '321,123')
    [IO.File]::WriteAllText((Join-Path $inst 'state\sessions\legacy.json'), '{"v":1,"session_id":"legacy","state":"DONE","event":"Stop","detail":"","project":"Website","updated":"2026-10-20T10:00:00.0000000Z"}')
    $settingsBefore = Get-Content $settingsPath -Raw
    $backupsBefore = @(Get-ChildItem $cd -Filter 'settings.json.bak*').Count
} else { Skip 'update from previous version' 'git not found' }

$out = & $winPs -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'install.ps1') -ClaudeDir $cd -NoStart 2>&1
Check 'new version installs / updates' ($LASTEXITCODE -eq 0) ($out | Out-String)
Check 'application files replaced with the new version' ((Get-Content (Join-Path $inst 'indicator.cs') -Raw) -match 'Worker Board')
$c = Get-Content (Join-Path $inst 'config.json') -Raw | ConvertFrom-Json
if ($haveGit) {
    Check 'update keeps existing sound / staleHours choices' ($c.soundOnDone -eq $false -and $c.staleHours -eq 12)
    Check 'update keeps the saved window position and existing state files' ((Get-Content (Join-Path $inst 'position.txt')) -eq '321,123' -and (Test-Path (Join-Path $inst 'state\sessions\legacy.json')))
    Check 'hooks unchanged by the update: settings.json untouched, no backup' ((Get-Content $settingsPath -Raw) -eq $settingsBefore -and @(Get-ChildItem $cd -Filter 'settings.json.bak*').Count -eq $backupsBefore)
}
Check 'new settings added with defaults (Worker Board, urgency, summary off)' ($c.startupView -eq 'workerBoard' -and $c.workerSort -eq 'urgency' -and $c.showWorkerSummary -eq $false)
Check 'project aliases added' ($c.projectAliases.Toah_Nipi_Booking_Software -eq 'Booking Software' -and $c.projectAliases.Guest_Booking_Form -eq 'Guest Form')
$s = Get-Content $settingsPath -Raw | ConvertFrom-Json
$ours = 0; foreach ($p in $s.hooks.PSObject.Properties) { foreach ($grp in $p.Value) { foreach ($hk in $grp.hooks) { if ((@($hk.args) -join ' ') -match 'status-indicator\\hook\.ps1') { $ours++ } } } }
Check 'exactly 9 Claude Status hooks, unrelated hook and settings kept' ($ours -eq 9 -and $s.model -eq 'opus' -and ((Get-Content $settingsPath -Raw) -match 'my-own-hook'))
$cfgText = Get-Content (Join-Path $inst 'config.json') -Raw
$out = & $winPs -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'install.ps1') -ClaudeDir $cd -NoStart 2>&1
Check 're-running the installer is a no-op for settings.json and config.json' ((Get-Content (Join-Path $inst 'config.json') -Raw) -eq $cfgText -and (($out | Out-String) -match 'already up to date'))
Check 'phone dashboard files installed; config gets phoneDashboard=false, phonePort=48521' ((@('phone.cs', 'phone.html', 'phone.css', 'phone.js', 'phone-setup.ps1') | Where-Object { -not (Test-Path (Join-Path $inst $_)) }).Count -eq 0 -and $c.phoneDashboard -eq $false -and $c.phonePort -eq 48521)
$out = & $winPs -NoProfile -Command "Add-Type -Path '$inst\indicator.cs', '$inst\phone.cs' -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Web.Extensions; 'ok'" 2>&1
Check 'installed indicator compiles with Windows PowerShell 5.1' (($out | Out-String).Trim() -eq 'ok') ($out | Out-String)
$auto = & $winPs -NoProfile -ExecutionPolicy Bypass -File (Join-Path $inst 'autostart.ps1') 2>&1
Check 'autostart script runs (status query only; your Startup folder is not modified)' (($auto | Out-String) -match 'Autostart is currently')

$setupPs = Join-Path $inst 'phone-setup.ps1'
$out = & $winPs -NoProfile -ExecutionPolicy Bypass -File $setupPs -Status 2>&1 | Out-String
Check 'phone-setup.ps1 -Status reports without changing anything' ($LASTEXITCODE -eq 0 -and $out -match 'phoneDashboard = False' -and (Get-Content (Join-Path $inst 'config.json') -Raw) -eq $cfgText) $out
# -NoServe: config only; Tailscale is never touched by the tests. (The indicator isn't started here: -NoStart above,
# and phone-setup would start it, so point it at a port and accept the "not answering" warning.)
$out = & $winPs -NoProfile -ExecutionPolicy Bypass -Command "function Start-Process { }; & '$setupPs' -NoServe" 2>&1 | Out-String
$c2 = Get-Content (Join-Path $inst 'config.json') -Raw | ConvertFrom-Json
Check 'phone-setup.ps1 -NoServe turns phoneDashboard on, keeps every other setting' ($c2.phoneDashboard -eq $true -and $c2.staleHours -eq $c.staleHours -and $c2.projectAliases.Toah_Nipi_Portal -eq 'Portal') $out
$out = & $winPs -NoProfile -ExecutionPolicy Bypass -File $setupPs -Disable 2>&1 | Out-String
Check 'phone-setup.ps1 -Disable turns it off; leaves tailscale serve alone when this install never set it up' ((Get-Content (Join-Path $inst 'config.json') -Raw | ConvertFrom-Json).phoneDashboard -eq $false -and $out -notmatch 'removed tailscale serve') $out

$out = & $winPs -NoProfile -ExecutionPolicy Bypass -File (Join-Path $inst 'uninstall.ps1') -ClaudeDir $cd 2>&1
$s = Get-Content $settingsPath -Raw
Check 'uninstall removes the app folder' (-not (Test-Path $inst)) ($out | Out-String)
Check 'uninstall removes only Claude Status hooks' ($s -notmatch 'status-indicator' -and $s -match 'my-own-hook' -and $s -match 'opus')

# ======================================================================================
Section 'Privacy / no-network audit (static)'
# ======================================================================================
$phoneSrc = 'phone.cs', 'phone.js', 'phone.html', 'phone.css', 'phone-setup.ps1'
$code = (Get-ChildItem $src -File | Where-Object { $phoneSrc -notcontains $_.Name } | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
Check 'no network APIs in the desktop app or hook' ($code -notmatch 'WebClient|HttpClient|WebRequest|Invoke-WebRequest|Invoke-RestMethod|Net\.Sockets|TcpClient|TcpListener|UdpClient|https?://')
$pcs = Get-Content (Join-Path $src 'phone.cs') -Raw
Check 'phone server only listens on loopback and never connects out' ($pcs -match 'new TcpListener\(IPAddress\.Loopback' -and $pcs -notmatch 'IPAddress\.(Any|IPv6Any)|0\.0\.0\.0|new TcpClient|\.Connect\(|WebClient|HttpClient|WebRequest|Access-Control-Allow')
$pjs = (Get-Content (Join-Path $src 'phone.js') -Raw) + (Get-Content (Join-Path $src 'phone.html') -Raw)
$pjsNoSvgNs = $pjs.Replace("'http://www.w3.org/2000/svg'", "''")   # the SVG namespace name is an identifier, not a request
Check 'phone page: no notifications, sounds, vibration, speech, service worker, other storage or outside URLs' ($pjsNoSvgNs -cnotmatch 'Notification|vibrate|Audio|speechSynthesis|serviceWorker|sessionStorage|indexedDB|document\.cookie|innerHTML|https?://')
$storage = @([regex]::Matches($pjs, 'localStorage\.\w+\([^)]*\)') | ForEach-Object { $_.Value })
Check 'phone page stores only the List / Creatures choice' ($storage.Count -eq 2 -and @($storage | Where-Object { $_ -notmatch '^localStorage\.(getItem|setItem)\(VIEW_KEY(, view)?\)$' }).Count -eq 0 -and $pjs -match "VIEW_KEY = 'claudeStatus\.view'") ($storage -join '; ')
Check 'phone page only calls its own relative API' ((([regex]::Matches($pjs, "'(api/[a-z]*)") | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) -join ',') -eq 'api/board,api/dismiss,api/focus' -and $pjs -cnotmatch 'XMLHttpRequest|WebSocket|EventSource|sendBeacon|fetch\(\s*[''"](?!api/)')
$setup = Get-Content (Join-Path $src 'phone-setup.ps1') -Raw
Check 'phone-setup.ps1 never runs Funnel and only proxies to 127.0.0.1' ($setup -notmatch '(?i)[''"]funnel[''"]|\bfunnel\s+(on|--)' -and @([regex]::Matches($setup, 'https?://[^\s''"`]+') | Where-Object { $_.Value -notmatch '^http://(127\.0\.0\.1|localhost):' }).Count -eq 0)
Check 'indicator never reads window titles' ($code -notmatch 'GetWindowText' -and $pcs -notmatch 'GetWindowText')
Check 'hook never reads the prompt / message / tool input fields; unchanged by the phone feature' ((Get-Content (Join-Path $src 'hook.ps1') -Raw) -notmatch 'phone' -and $code -notmatch '\$in\.(prompt|last_assistant_message|tool_input|tool_response|message|transcript_path|error_details)')

# ======================================================================================
if ($E2E) {
    Section 'End-to-end: real `claude -p` with hooks pointed at a temporary install'
    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { Skip 'e2e' 'claude not on PATH' }
    else {
        $cd2 = Join-Path $tmp 'claude-e2e'; $null = New-Item -ItemType Directory -Force $cd2
        $null = & $winPs -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'install.ps1') -ClaudeDir $cd2 -NoStart 2>&1
        $proj = Join-Path $tmp 'Toah_Nipi_Website'; $null = New-Item -ItemType Directory -Force $proj
        Push-Location $proj
        try { $null = '' | claude -p 'Reply with just the word ok. E2E-SECRET-PROMPT-4242' --settings (Join-Path $cd2 'settings.json') --setting-sources project 2>&1 }
        finally { Pop-Location }
        $files = @(Get-ChildItem (Join-Path $cd2 'status-indicator\state\sessions') -Filter *.json)
        Check 'e2e: one session file written' ($files.Count -eq 1)
        if ($files.Count -eq 1) {
            $txt = Get-Content $files[0].FullName -Raw; $j = $txt | ConvertFrom-Json
            Check 'e2e: DONE retained after the session ended' ($j.state -eq 'DONE')
            Check 'e2e: v2 with the real claude process identity' ($j.v -eq 2 -and $j.claude_pid -gt 0 -and $j.claude_started)
            Check 'e2e: project folder name only' ($j.project -eq 'Toah_Nipi_Website')
            Check 'e2e: prompt not stored' ($txt -notmatch 'E2E-SECRET')
            Info $txt.Trim()
        }
    }
}

# ======================================================================================
Write-Host ''
Write-Host ("RESULT: {0} passed, {1} failed, {2} skipped" -f $script:pass, $script:fail, $script:skip) -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
if ($script:fail) { $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red } }
try { Get-ChildItem $tmp -Exclude 'renders' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue } catch {}
exit $(if ($script:fail) { 1 } else { 0 })

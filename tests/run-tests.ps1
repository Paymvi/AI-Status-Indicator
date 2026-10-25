<#
.SYNOPSIS
    Automated tests for Claude Status (hook lifecycle, Worker Board logic, layout/DPI, worker focusing,
    installer / update / uninstall, privacy).

.DESCRIPTION
    Everything runs against temporary folders; your real %USERPROFILE%\.claude is never touched.
    -Live      also runs live-tests.ps1: opens real console and test windows and moves keyboard focus
               between them (about 30 seconds; don't type while it runs).
    -E2E       also runs one real headless `claude -p` prompt with the hooks pointed at a temporary install.
    -RenderDir where to save PNG renders of the Worker Board at each DPI scale (for visual review).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -Live
#>
param([switch]$Live, [switch]$E2E, [string]$RenderDir)

if ($PSVersionTable.PSEdition -eq 'Core') {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($Live) { $a += '-Live' }; if ($E2E) { $a += '-E2E' }; if ($RenderDir) { $a += @('-RenderDir', $RenderDir) }
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
Check 'empty: tray says IDLE' ($f.TrayText -eq 'Claude: IDLE')
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
Check 'Compact/tray surface the most urgent worker' ($f.Surfaced.Id -eq 'ny' -and $f.TrayText -eq 'Claude: NEEDS YOU (Portal)')
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
Check 'settings window starts with Urgency / summary off / Worker Board selected' ((& $radio 'Urgency').Checked -and -not (& $radio 'Most recent').Checked -and (& $radio 'Worker Board').Checked -and -not ($ctrls | Where-Object { $_ -is [System.Windows.Forms.CheckBox] }).Checked)
(& $radio 'Most recent').Checked = $true
($ctrls | Where-Object { $_ -is [System.Windows.Forms.CheckBox] }).Checked = $true
(& $radio 'Compact').Checked = $true
Check 'settings window: ordering / summary / startup view apply immediately' ($changes['workerSort'] -eq 'recent' -and $changes['showWorkerSummary'] -eq $true -and $changes['startupView'] -eq 'compact')
Check 'settings window stays small' ($sf.Width -lt 0.4 * [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Width -and $sf.Height -lt 0.6 * [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Height) "$($sf.Size)"
$sf.Dispose()
$f = Form (New-Root)
$f.GetType().GetMethod('BuildMenu', [Reflection.BindingFlags]'NonPublic,Instance').Invoke($f, @())
$menu = $f.GetType().GetField('menu', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($f)
$view = $menu.Items | Where-Object { $_.Text -eq 'View' }
Check 'tray / right-click menu: View > Worker Board (checked) / Compact' ($view -and $view.DropDownItems[0].Text -eq 'Worker Board' -and $view.DropDownItems[0].Checked -and $view.DropDownItems[1].Text -eq 'Compact' -and -not $view.DropDownItems[1].Checked)
$texts = @($menu.Items | ForEach-Object { $_.Text })
Check 'menu keeps Settings, Show window, Reset position, Exit; no row actions' (($texts -like 'Settings*') -and ($texts -contains 'Show window') -and ($texts -contains 'Reset position') -and ($texts -contains 'Exit') -and -not ($texts -match 'Kill|Stop|Restart|Dismiss'))
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
Check 'Compact hit areas: close / settings / view toggle / drag' ($f.HitAt((New-Object System.Drawing.Point(115, 8))) -eq 'close' -and $f.HitAt((New-Object System.Drawing.Point(99, 8))) -eq 'gear' -and $f.HitAt((New-Object System.Drawing.Point(83, 8))) -eq 'toggle' -and $f.HitAt((New-Object System.Drawing.Point(30, 28))) -eq 'drag')
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
    foreach ($fn in 'install.ps1', 'src/hook.ps1', 'src/indicator.cs', 'src/indicator.ps1', 'src/start-indicator.vbs', 'src/stop-indicator.ps1', 'src/autostart.ps1', 'src/uninstall.ps1', 'src/config.json') {
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
$out = & $winPs -NoProfile -Command "Add-Type -Path '$inst\indicator.cs' -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Web.Extensions; 'ok'" 2>&1
Check 'installed indicator compiles with Windows PowerShell 5.1' (($out | Out-String).Trim() -eq 'ok') ($out | Out-String)
$auto = & $winPs -NoProfile -ExecutionPolicy Bypass -File (Join-Path $inst 'autostart.ps1') 2>&1
Check 'autostart script runs (status query only; your Startup folder is not modified)' (($auto | Out-String) -match 'Autostart is currently')

$out = & $winPs -NoProfile -ExecutionPolicy Bypass -File (Join-Path $inst 'uninstall.ps1') -ClaudeDir $cd 2>&1
$s = Get-Content $settingsPath -Raw
Check 'uninstall removes the app folder' (-not (Test-Path $inst)) ($out | Out-String)
Check 'uninstall removes only Claude Status hooks' ($s -notmatch 'status-indicator' -and $s -match 'my-own-hook' -and $s -match 'opus')

# ======================================================================================
Section 'Privacy / no-network audit (static)'
# ======================================================================================
$code = (Get-ChildItem $src -File | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
Check 'no network APIs in the app' ($code -notmatch 'WebClient|HttpClient|WebRequest|Invoke-WebRequest|Invoke-RestMethod|Net\.Sockets|TcpClient|UdpClient|https?://')
Check 'indicator never reads window titles' ($code -notmatch 'GetWindowText')
Check 'hook never reads the prompt / message / tool input fields' ($code -notmatch '\$in\.(prompt|last_assistant_message|tool_input|tool_response|message|transcript_path|error_details)')

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

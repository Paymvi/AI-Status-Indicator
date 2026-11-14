# Calendar tests (dot-sourced by run-tests.ps1 after common.ps1 and the hook section, whose Invoke-Hook it reuses).
# Fixtures live in temporary folders only; your real history, Codex logs and Queue are never read or written, and no
# scheduled prompt is ever run.

$PR = [ClaudeStatus.PromptRuns]; $CAL = [ClaudeStatus.Calendar]
# Delegates the calendar calls from other threads (the phone server's) must not be PowerShell script blocks.
# (The product types live in another in-memory assembly, so the constant one is generic.)
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic;
public static class TCal {
    public static Func<DateTime> At(DateTime t) { return () => t; }
    public static Func<T> Const<T>(T v) { return () => v; }
    public static Func<Dictionary<string, object>> Cfg(Dictionary<string, object> c) { return () => c; }
    public static Func<string, bool> Live(string[] ids) { var h = new HashSet<string>(ids); return s => h.Contains(s); }
}
'@ -ErrorAction SilentlyContinue
function QData($d) { [TCal].GetMethod('Const').MakeGenericMethod([ClaudeStatus.QueueData]).Invoke($null, [object[]]@($d.PSObject.BaseObject)) }
$pst = [TimeZoneInfo]::FindSystemTimeZoneById('Pacific Standard Time')
$T0 = New-Object DateTime(2026, 11, 10, 17, 0, 0, [DateTimeKind]::Utc)   # Tue 10 Nov 2026, 9:00 AM Pacific
$script:evn = 0
function E([string]$s, [string]$e, [double]$min, [string]$pj = 'Toah_Nipi_Website', [string]$d = '', [string]$p = 'claude', [string]$id = '') {
    $script:evn++
    $x = New-Object ClaudeStatus.ActEvent
    $x.Id = $(if ($id) { $id } else { "ev$($script:evn)" }); $x.P = $p; $x.S = $s; $x.E = $e; $x.T = $T0.AddMinutes($min); $x.Pj = $pj; $x.D = $d
    return $x
}
function EvList { $l = New-Object 'System.Collections.Generic.List[ClaudeStatus.ActEvent]'; foreach ($x in $args) { foreach ($y in @($x)) { $l.Add($y) } }; return , $l }
$alive = [Func[ClaudeStatus.PromptRun, bool]] { param($r) $true }
$dead = [Func[ClaudeStatus.PromptRun, bool]] { param($r) $false }
function Fold($events, [DateTime]$now, $live = $dead, [double]$stale = 24) { , $PR::Fold([ClaudeStatus.ActEvent[]]@($events), $now, $live, $stale) }
function RunList { $l = New-Object 'System.Collections.Generic.List[ClaudeStatus.PromptRun]'; foreach ($x in $args) { foreach ($y in @($x)) { $l.Add($y) } }; return , $l }

# ======================================================================================
Section 'Calendar: prompt lifecycle (events -> one block per prompt)'
# ======================================================================================
$r = Fold (EvList (E 'a' 'start' 0) (E 'a' 'tick' 3) (E 'a' 'wait' 5 -d 'Bash') (E 'a' 'resume' 9) (E 'a' 'wait' 12 -d 'AskUserQuestion') (E 'a' 'resume' 13) (E 'a' 'done' 20)) $T0.AddHours(1)
Check 'one prompt from start to Stop = one completed run' ($r.Count -eq 1 -and $r[0].Status -eq 'completed' -and $r[0].EndKnown -and $r[0].Start -eq $T0 -and $r[0].End -eq $T0.AddMinutes(20))
Check 'two pauses recorded inside the same run, each closed when work resumed' ($r[0].Waits.Count -eq 2 -and $r[0].Waits[0].From -eq $T0.AddMinutes(5) -and $r[0].Waits[0].To -eq $T0.AddMinutes(9) -and $r[0].Waits[1].Detail -eq 'AskUserQuestion')
Check 'elapsed includes waiting; active excludes it (20 min, 5 min waiting)' ($r[0].Waited($T0).TotalMinutes -eq 5)
$f = $CAL::Facts($r[0], 'Website', 'toahnipi', $T0.AddHours(1), $pst)
$fx = @{}; foreach ($x in $f) { $fx[$x[0]] = $x[1] }
Check 'details: elapsed 20 min, active 15 min, waiting 5 min (2 pauses), recorded by hooks' ($fx['Elapsed'] -eq '20 min' -and $fx['Active'] -eq '15 min' -and $fx['Waiting'] -eq '5 min (2 pauses)' -and $fx['Recorded by'] -eq 'Claude Code hooks') ($f | ForEach-Object { $_ -join '=' })

$r = Fold (EvList (E 'b' 'start' 0) (E 'b' 'done' 10) (E 'b' 'start' 30) (E 'b' 'done' 31)) $T0.AddHours(1)
Check 'a later prompt in the same conversation is a new block' ($r.Count -eq 2 -and $r[1].Start -eq $T0.AddMinutes(30) -and $r[0].Id -ne $r[1].Id)

$r = Fold (EvList (E 'c' 'start' 0) (E 'c' 'wait' 4)) $T0.AddMinutes(30) $alive
Check 'still on it and its session is live: running, currently waiting (no end yet)' ($r[0].Status -eq 'waiting' -and $r[0].End -eq [DateTime]::MinValue -and $r[0].Live)
Check 'a live run grows with the clock (elapsed = now - start)' (($r[0].Until($T0.AddMinutes(30)) - $r[0].Start).TotalMinutes -eq 30 -and $r[0].Waited($T0.AddMinutes(30)).TotalMinutes -eq 26)

$r = Fold (EvList (E 'd' 'start' 0) (E 'd' 'tick' 7)) $T0.AddHours(2) $dead
Check 'crash / lost tracking (session gone, no finish): ends at the last thing seen, marked not recorded' ($r[0].Status -eq 'lost' -and -not $r[0].EndKnown -and $r[0].End -eq $T0.AddMinutes(7))
$fx = @{}; foreach ($x in $CAL::Facts($r[0], 'W', 'other', $T0.AddHours(2), $pst)) { $fx[$x[0]] = $x[1] }
Check 'an unrecorded ending is never shown as a finish time: "Not recorded", durations "at least"' ($fx['Ended'] -like 'Not recorded*' -and $fx['Elapsed'] -eq 'at least 7 min')
$r = Fold (EvList (E 'd2' 'start' 0)) $T0.AddHours(30) $alive 24
Check 'a run with no event for longer than staleHours never keeps growing, even if the session looks alive' ($r[0].Status -eq 'lost' -and $r[0].End -eq $T0)

$r = Fold (EvList (E 'e' 'start' 0) (E 'e' 'tick' 4) (E 'e' 'start' 50) (E 'e' 'done' 55)) $T0.AddHours(1)
Check 'Esc (no hook) then a new prompt: the first ends at its last activity, marked interrupted (not 50 min long)' ($r.Count -eq 2 -and $r[0].Status -eq 'interrupted' -and -not $r[0].EndKnown -and $r[0].End -eq $T0.AddMinutes(4))
$r = Fold (EvList (E 'f' 'start' 0) (E 'f' 'wait' 2) (E 'f' 'end' 6)) $T0.AddHours(1)
Check 'session closed mid-prompt: interrupted at the close, the open wait closes with it' ($r[0].Status -eq 'interrupted' -and $r[0].EndKnown -and $r[0].End -eq $T0.AddMinutes(6) -and $r[0].Waits[0].To -eq $T0.AddMinutes(6))
$r = Fold (EvList (E 'g' 'start' 0) (E 'g' 'fail' 3 -d 'rate_limit')) $T0.AddHours(1)
Check 'StopFailure: failed, with the error type' ($r[0].Status -eq 'failed' -and $r[0].Detail -eq 'rate_limit' -and ($CAL::StatusLabel($r[0], $T0)) -eq 'Failed (rate_limit)')
$r = Fold (EvList (E 'h' 'done' 1) (E 'h' 'resume' 2) (E 'h' 'start' 5) (E 'h' 'done' 9) (E 'h' 'resume' 9.5) (E 'h' 'wait' 10)) $T0.AddHours(1)
Check 'a finish with no start seen, and async events after Stop, create nothing' ($r.Count -eq 1 -and $r[0].End -eq $T0.AddMinutes(9) -and $r[0].Waits.Count -eq 0)
$r = Fold (EvList (E 'i' 'start' 0) (E 'i' 'wait' 1) (E 'i' 'tick' 3) (E 'i' 'done' 5)) $T0.AddHours(1)
Check 'a lost resume: the next tool activity ends the wait' ($r[0].Waits[0].To -eq $T0.AddMinutes(3))
$r = Fold (EvList (E 'j' 'start' 0) (E 'j' 'done' 0.05)) $T0.AddHours(1)
Check 'a 3-second prompt is a real, tiny run' ($r[0].Status -eq 'completed' -and ($r[0].End - $r[0].Start).TotalSeconds -eq 3)

# ======================================================================================
Section 'Calendar: persistence and de-duplication'
# ======================================================================================
$adir = Join-Path $tmp 'cal-log'
$log = New-Object ClaudeStatus.ActivityLog($adir)
$evs = EvList (E 'p' 'start' 0 -id 'p1') (E 'p' 'wait' 2 -id 'p2') (E 'p' 'resume' 4 -id 'p3') (E 'p' 'done' 9 -id 'p4')
$log.Append($evs); $log.Append($evs)   # written twice (a retry, a re-read)
$back = $log.Read($T0.AddDays(-1), $T0.AddDays(1))
Check 'events survive a restart (a fresh reader reads them back)' ($back.Count -eq 8 -and (New-Object ClaudeStatus.ActivityLog($adir)).Read($T0.AddDays(-1), $T0.AddDays(1)).Count -eq 8)
$r = Fold $back $T0.AddHours(1)
Check 'the same events written twice still make one block' ($r.Count -eq 1 -and $r[0].Waits.Count -eq 1)
$file = Join-Path $adir ([ClaudeStatus.ActivityLog]::DayFile($T0))
[IO.File]::AppendAllText($file, '{"id":"half","p":"claude","s":"p","e":"st')
Check 'a half-written last line (a hook mid-write) is skipped, the rest still reads' (($log.Read($T0.AddDays(-1), $T0.AddDays(1))).Count -eq 8)
[IO.File]::AppendAllText($file, "art`",`"t`":`"$($T0.AddMinutes(30).ToString('o'))`"}`n")
Check 'once completed, that line is read (the cache notices the file grew)' (($log.Read($T0.AddDays(-1), $T0.AddDays(1))).Count -eq 9)
[IO.File]::AppendAllText($file, "not json`n{`"id`":`"x`"}`n")
Check 'garbage lines are ignored' (($log.Read($T0.AddDays(-1), $T0.AddDays(1))).Count -eq 9)
$late = New-Object DateTime(2026, 11, 11, 7, 30, 0, [DateTimeKind]::Utc)   # 11:30 PM Pacific on the 10th = next UTC day
$log.Append((EvList (E 'q' 'start' ($late - $T0).TotalMinutes -id 'q1') (E 'q' 'done' (($late - $T0).TotalMinutes + 90) -id 'q2')))
Check 'timestamps are stored in UTC, one file per UTC day' ((Test-Path (Join-Path $adir '2026-11-11.jsonl')) -and ((Get-Content (Join-Path $adir '2026-11-11.jsonl') -Raw) -match '"t":"2026-11-11T07:30:00(\.0+)?Z"'))

# --- hook.ps1, run as Claude Code runs it ---
$hk = Join-Path $hookDir 'state\activity'
$sid = 'cal-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$secretCal = Ev $sid 'UserPromptSubmit' @{ prompt = 'CAL-SECRET-PROMPT-5150'; cwd = 'C:\Users\Someone\Private Stuff\Toah_Nipi_Portal' }
$null = Invoke-Hook 'working' $secretCal
$null = Invoke-Hook 'resume' (Ev $sid 'PostToolUse')
$null = Invoke-Hook 'resume' (Ev $sid 'PostToolUse')   # within 2 minutes: no second tick
$null = Invoke-Hook 'needs_you' (Ev $sid 'Notification' @{ notification_type = 'permission_prompt' })
$null = Invoke-Hook 'resume' (Ev $sid 'PostToolUse')
$null = Invoke-Hook 'done' (Ev $sid 'Stop')
$null = Invoke-Hook 'end' (Ev $sid 'SessionEnd')
$hlog = New-Object ClaudeStatus.ActivityLog($hk)
$mine = @($hlog.Read([DateTime]::UtcNow.AddHours(-1), [DateTime]::UtcNow.AddMinutes(1)) | Where-Object { $_.S -eq $sid })
Check 'hook: start, one throttled tick, wait (with its type), resume, done, end' ((($mine | ForEach-Object { $_.E }) -join ',') -eq 'start,tick,wait,resume,done,end' -and $mine[2].D -eq 'permission_prompt') (($mine | ForEach-Object { $_.E }) -join ',')
$hr = Fold $mine ([DateTime]::UtcNow)
Check 'hook events fold into one completed run with one pause' ($hr.Count -eq 1 -and $hr[0].Status -eq 'completed' -and $hr[0].Waits.Count -eq 1 -and $hr[0].Project -eq 'Toah_Nipi_Portal')
$rawLog = (Get-ChildItem $hk -Filter *.jsonl | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
Check 'privacy: no prompt text and no full path in the activity log (folder name only)' ($rawLog -notmatch 'CAL-SECRET' -and $rawLog -notmatch 'Private Stuff' -and $rawLog -notmatch 'Someone')
# simultaneous hooks (several workers at once) never lose or mangle a line
$sids = 1..6 | ForEach-Object { 'par-' + $_ + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6) }
$env:CLAUDE_PID = "$fakeClaude"
$jobs = foreach ($s in $sids) {
    $jf = Join-Path $tmp "par-$s.json"
    [IO.File]::WriteAllText($jf, ((Ev $s 'UserPromptSubmit') | ConvertTo-Json -Compress))
    Start-Process $winPs -ArgumentList '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "`"$hook`"", 'working' -RedirectStandardInput $jf -RedirectStandardOutput (Join-Path $tmp "par-$s.out") -PassThru -NoNewWindow
}
$jobs | ForEach-Object { $_.WaitForExit(30000) | Out-Null }
Remove-Item Env:CLAUDE_PID -ErrorAction SilentlyContinue
$par = @($hlog.Read([DateTime]::UtcNow.AddHours(-1), [DateTime]::UtcNow.AddMinutes(1)) | Where-Object { $sids -contains $_.S })
$lines = @((Get-ChildItem $hk -Filter *.jsonl | ForEach-Object { Get-Content $_.FullName }) | Where-Object { $_ -match '"s":"par-' })
Check 'six hooks appending at once: six intact lines' ($par.Count -eq 6 -and $lines.Count -eq 6) "parsed=$($par.Count) lines=$($lines.Count)"

# ======================================================================================
Section 'Calendar: project categories'
# ======================================================================================
$ov = New-Object 'System.Collections.Generic.Dictionary[string,object]'; $ov['Guest_Booking_Form'] = 'toahnipi'; $ov['Toah_Nipi_Sandbox'] = 'other'
Check 'Toah Nipi folders are green (website, booking, portal, analytics, staff app spellings)' (@(@('Toah_Nipi_Website', 'Toah_Nipi_Booking_Software', 'Toah_Nipi_Portal', 'Toah_Nipi_Website_Analytics', 'toah-nipi-staff-app', 'ToahNipiGuestPortal') | Where-Object { $CAL::Category($_, $null) -ne 'toahnipi' }).Count -eq 0)
Check 'a generic "Portal" (or "Website") project is not Toah Nipi; aliases never decide' ($CAL::Category('Portal', $null) -eq 'other' -and $CAL::Category('Website', $null) -eq 'other' -and $CAL::Category('Velo', $null) -eq 'other')
Check 'an explicit category wins both ways (Guest_Booking_Form -> Toah Nipi; a Toah Nipi-named folder -> other), case-insensitive' ($CAL::Category('guest_booking_form', $ov) -eq 'toahnipi' -and $CAL::Category('Toah_Nipi_Sandbox', $ov) -eq 'other')
$defaults = Get-Content (Join-Path $src 'config.json') -Raw | ConvertFrom-Json
Check 'default config: Guest_Booking_Form (repo Toah_Nipi_Guest_Booking_Form) counts as Toah Nipi; Include Codex is on' ($defaults.projectCategories.Guest_Booking_Form -eq 'toahnipi' -and $defaults.includeCodex -eq $true)

# ======================================================================================
Section 'Calendar: week layout (overlaps, tiny runs, overnight, empty days, DST)'
# ======================================================================================
$wk = New-Object DateTime(2026, 11, 8)   # Sunday
function Build($runs, $queue = $null, [DateTime]$now = $T0.AddDays(3), [bool]$codex = $true, $cats = $null, [DateTime]$start = $wk) { $CAL::Build($start, $runs, $queue, $cats, $now, $pst, $codex) }
$ov3 = Fold (EvList (E 'o1' 'start' 0 -pj 'Toah_Nipi_Website') (E 'o1' 'done' 60) (E 'o2' 'start' 10 -pj 'Velo') (E 'o2' 'done' 40) (E 'o3' 'start' 20 -pj 'Toah_Nipi_Portal') (E 'o3' 'done' 30) (E 'o4' 'start' 120) (E 'o4' 'done' 130)) $T0.AddHours(5)
$w = Build $ov3
$tue = @($w.Blocks | Where-Object { $_.Day -eq 2 } | Sort-Object StartMin)
Check 'seven days shown, including empty ones (only Tuesday has blocks)' ($w.Days.Count -eq 7 -and @($w.Blocks | Where-Object { $_.Day -ne 2 }).Count -eq 0)
Check 'three simultaneous workers sit side by side (3 columns), a later one gets the full width' ($tue[0].Cols -eq 3 -and $tue[1].Cols -eq 3 -and $tue[2].Cols -eq 3 -and (@($tue[0..2] | ForEach-Object { $_.Col } | Sort-Object -Unique).Count -eq 3) -and $tue[3].Cols -eq 1)
Check 'blocks are placed at their real times (9:00 AM Pacific = minute 540)' ($tue[0].StartMin -eq 540 -and $tue[0].EndMin -eq 600)
Check 'colors: Toah Nipi green, other yellow' ($tue[0].Cat -eq 'toahnipi' -and (@($tue | Where-Object { $_.Run.Project -eq 'Velo' })[0].Cat -eq 'other'))
$tiny = Fold (EvList (E 't1' 'start' 200) (E 't1' 'done' 200.3) (E 't2' 'start' 203) (E 't2' 'done' 203.2)) $T0.AddHours(5)
$tb = @((Build $tiny).Blocks | Sort-Object StartMin)
Check 'very short runs keep their true times (18 s) but don''t overlap when drawn at the minimum height' ([Math]::Round($tb[0].EndMin - $tb[0].StartMin, 2) -eq 0.3 -and $tb[0].Cols -eq 2 -and $tb[1].Col -eq 1)
$night = Fold (EvList (E 'n' 'start' (14 * 60 + 30) -pj 'Velo') (E 'n' 'wait' (15 * 60 + 50)) (E 'n' 'resume' (16 * 60 + 10)) (E 'n' 'done' (16 * 60 + 15))) $T0.AddDays(1)   # 11:30 PM -> 1:15 AM Pacific
$nb = @((Build $night).Blocks | Sort-Object Day)
Check 'overnight run: one record, drawn as two pieces (to midnight, from midnight)' ($nb.Count -eq 2 -and $nb[0].Run.Id -eq $nb[1].Run.Id -and $nb[0].Day -eq 2 -and $nb[0].StartMin -eq 1410 -and $nb[0].EndMin -eq 1440 -and $nb[0].ToNext -and $nb[1].FromPrev -and $nb[1].StartMin -eq 0 -and $nb[1].EndMin -eq 75 -and (Build $night).Runs.Count -eq 1)
Check 'its waiting part lands on the right day, at the right time (12:50-1:10 AM)' ($nb[1].Waits.Count -eq 1 -and $nb[1].Waits[0][0] -eq 50 -and $nb[1].Waits[0][1] -eq 70 -and $nb[0].Waits.Count -eq 0)
$dstWk = New-Object DateTime(2026, 11, 1)   # DST ends 1 Nov 2026, 2:00 AM Pacific
$dT = New-Object DateTime(2026, 11, 1, 8, 40, 0, [DateTimeKind]::Utc)   # 1:40 AM PDT
$dst = Fold (EvList (New-Object ClaudeStatus.ActEvent -Property @{ Id = 'd1'; P = 'claude'; S = 'dst'; E = 'start'; T = $dT; Pj = 'Velo' }) (New-Object ClaudeStatus.ActEvent -Property @{ Id = 'd2'; P = 'claude'; S = 'dst'; E = 'done'; T = $dT.AddMinutes(40); Pj = 'Velo' })) $dT.AddHours(5)   # ends 1:20 AM PST
$db = @((Build $dst $null $dT.AddHours(5) $true $null $dstWk).Blocks)
Check 'DST fall-back: a run through the repeated hour keeps its real 40 min (never negative), on the right day' ($db.Count -eq 1 -and $db[0].Day -eq 0 -and $db[0].StartMin -eq 100 -and [Math]::Round($db[0].EndMin - $db[0].StartMin) -eq 40)
$dw = Build (RunList) $null $dT $true $null $dstWk
Check 'DST week: seven local days, the zone named for display' ($dw.Days.Count -eq 7 -and $dw.Days[0] -eq $dstWk -and $dw.Zone -match 'Pacific')
$e = Build (RunList) $null ($T0.AddDays(30))
Check 'an empty week: seven days, no blocks, a note saying so' ($e.Blocks.Count -eq 0 -and (($CAL::Notes($e, $pst)) -join ' ') -match 'No AI activity')

# ======================================================================================
Section 'Calendar: scheduled tasks (planned, linked to their actual run, missed, failed to start)'
# ======================================================================================
function Task([string]$id, [string]$state, [DateTime]$at, [string]$outcome = '') {
    $t = New-Object ClaudeStatus.QueueTask; $t.Id = $id; $t.Title = "Task $id"; $t.Project = 'C:\work\Toah_Nipi_Booking_Software'; $t.State = $state
    $t.Schedule = 'at'; $t.RunAtUtc = $at; $t.Outcome = $outcome; return $t
}
function Attempt($t, [string]$sid, [DateTime]$start, [DateTime]$end, [string]$outcome) {
    $a = New-Object ClaudeStatus.RunAttempt; $a.N = $t.Attempts.Count + 1; $a.SessionId = $sid; $a.StartedUtc = $start; $a.EndedUtc = $end; $a.Outcome = $outcome; $a.Runner = 'vscode'; $a.Phase = 'ended'
    $t.Attempts.Add($a); return $a
}
$q = New-Object ClaudeStatus.QueueData
$tA = Task 'ran' 'Completed' $T0.AddMinutes(-5) 'completed'; $null = Attempt $tA 'sched-sess' $T0 $T0.AddMinutes(25) 'completed'; $q.Tasks.Add($tA)
$tB = Task 'next' 'Scheduled' $T0.AddDays(1); $q.Tasks.Add($tB)
$tC = Task 'missed' 'Canceled' $T0.AddHours(3) 'missed'; $q.Tasks.Add($tC)
$tD = Task 'nostart' 'NeedsAttention' $T0.AddHours(4) 'vscode'; $tD.Reason = "Couldn't start it in VS Code."; $null = Attempt $tD '' $T0.AddHours(4) $T0.AddHours(4).AddSeconds(30) 'vscode'; $q.Tasks.Add($tD)
$tE = Task 'old' 'Completed' $T0.AddHours(6) 'completed'; $null = Attempt $tE 'old-sess' $T0.AddHours(6) $T0.AddHours(6).AddMinutes(12) 'completed'; $q.Tasks.Add($tE)
$tF = Task 'gone' 'Canceled' $T0.AddHours(7) 'canceled'; $q.Tasks.Add($tF)
$tG = Task 'draft' 'Draft' $T0.AddHours(8); $q.Tasks.Add($tG)
$runs = Fold (EvList (E 'sched-sess' 'start' 0.1 -pj 'Toah_Nipi_Booking_Software') (E 'sched-sess' 'done' 24) (E 'sched-sess' 'start' 40 -pj 'Toah_Nipi_Booking_Software') (E 'sched-sess' 'done' 45)) $T0.AddHours(9)
$w = Build $runs $q $T0.AddHours(9)
$linked = @($w.Runs | Where-Object { $_.TaskId -eq 'ran' })
Check 'a scheduled task''s run is linked to its actual prompt run (title, planned time), no duplicate planned block' ($linked.Count -eq 1 -and $linked[0].Title -eq 'Task ran' -and $linked[0].Source -eq 'hooks' -and $linked[0].PlannedUtc -eq $T0.AddMinutes(-5) -and @($w.Blocks | Where-Object { $_.Plan -and $_.Plan.TaskId -eq 'ran' }).Count -eq 0)
$fx = @{}; foreach ($x in $CAL::Facts($linked[0], 'Booking Software', 'toahnipi', $T0.AddHours(9), $pst)) { $fx[$x[0]] = $x[1] }
Check 'it started late: its real start is shown, and how late' ($linked[0].Start -eq $T0.AddMinutes(0.1) -and $fx['Planned for'] -match 'started 5 min late')
Check 'a later prompt you typed in that conversation stays a separate, unlinked block' (@($w.Runs | Where-Object { $_.Session -eq 'sched-sess' -and $_.TaskId -eq '' }).Count -eq 1)
$planB = @($w.Plans | Where-Object { $_.TaskId -eq 'next' })
Check 'an upcoming task: a tentative planned block at its scheduled start, a 30-min estimate' ($planB.Count -eq 1 -and $planB[0].Kind -eq 'planned' -and $planB[0].At -eq $T0.AddDays(1) -and (@($w.Blocks | Where-Object { $_.Plan -eq $planB[0] })[0].EndMin - @($w.Blocks | Where-Object { $_.Plan -eq $planB[0] })[0].StartMin) -eq 30)
$fx = @{}; foreach ($x in $CAL::Facts($planB[0], 'Booking Software', 'toahnipi', $pst)) { $fx[$x[0]] = $x[1] }
Check 'its details say the duration is unknown (no finish time implied)' ($fx['Duration'] -match '^Unknown' -and -not $fx.ContainsKey('Ended'))
Check 'missed, canceled and failed-to-start tasks are shown as such; drafts are not' ((@($w.Plans | Where-Object { $_.TaskId -eq 'missed' })[0].Label -eq 'Missed') -and (@($w.Plans | Where-Object { $_.TaskId -eq 'gone' })[0].Label -eq 'Canceled') -and (@($w.Plans | Where-Object { $_.TaskId -eq 'nostart' })[0].Label -eq "Didn't start") -and @($w.Plans | Where-Object { $_.TaskId -eq 'draft' }).Count -eq 0)
$rec = @($w.Runs | Where-Object { $_.TaskId -eq 'old' })
Check 'a run from before tracking (no lifecycle events): shown from the Queue''s own record, waiting marked unknown' ($rec.Count -eq 1 -and $rec[0].Source -eq 'scheduler' -and -not $rec[0].WaitsKnown -and $rec[0].Status -eq 'completed' -and ($rec[0].End - $rec[0].Start).TotalMinutes -eq 12)
$tB.RunAtUtc = $T0.AddDays(2)
Check 'a schedule edit moves the planned block (the Queue is the source of truth)' (@((Build $runs $q $T0.AddHours(9)).Plans | Where-Object { $_.TaskId -eq 'next' })[0].At -eq $T0.AddDays(2))
$null = $q.Tasks.Remove($tB)
Check 'a removed task leaves no planned block' (@((Build $runs $q $T0.AddHours(9)).Plans | Where-Object { $_.TaskId -eq 'next' }).Count -eq 0)

# Calendar viewing never runs anything: a task already due stays untouched however often the week is read.
# (No QueueEngine here: saving through it would tick the scheduler, which is exactly what must not happen.)
$qdir = Join-Path $tmp 'cal-queue'
$store = New-Object ClaudeStatus.QueueStore($qdir)
$dq = New-Object ClaudeStatus.QueueData
$due = Task 'due' 'Scheduled' ([DateTime]::UtcNow.AddMinutes(-1)); $due.Prompt = 'never run me'; $dq.Tasks.Add($due)
[IO.File]::WriteAllText($store.FilePath, [ClaudeStatus.QueueStore]::Ser().Serialize($dq))
$before = Get-Content $store.FilePath -Raw
$csrc = New-Object ClaudeStatus.CalendarSource; $csrc.Log = New-Object ClaudeStatus.ActivityLog((Join-Path $qdir 'activity')); $csrc.Queue = QData($store.Read())
$cweeks = 1..5 | ForEach-Object { $csrc.Week([DateTime]::Today) }
Check 'reading the calendar never starts, changes or reruns a scheduled task (shown as planned, file untouched)' ((Get-Content $store.FilePath -Raw) -eq $before -and @($store.Read().Tasks)[0].State -eq 'Scheduled' -and @($store.Read().Tasks)[0].Attempts.Count -eq 0 -and -not (Test-Path (Join-Path $qdir 'runs')) -and @($cweeks[4].Plans | Where-Object { $_.TaskId -eq 'due' }).Count -eq 1)

# ======================================================================================
Section 'Calendar: Codex (its own session logs) and Include Codex'
# ======================================================================================
$cx = Join-Path $tmp 'codex-home'; $cxd = Join-Path $cx 'sessions\2026\11\10'; $null = New-Item -ItemType Directory -Force $cxd
function CxLine([DateTime]$t, [string]$type, [string]$payload) { '{"timestamp":"' + $t.ToString('yyyy-MM-ddTHH:mm:ss.fffZ') + '","ordinal":1,"type":"' + $type + '","payload":' + $payload + '}' }
$main = Join-Path $cxd 'rollout-2026-11-10T09-00-00-thread-main.jsonl'
$lines = @(
    (CxLine $T0 'session_meta' '{"id":"thread-main","cwd":"c:\\Users\\x\\Toah_Nipi_Booking_Software","originator":"codex_vscode","source":"vscode","thread_source":"user","base_instructions":"task_started task_complete"}'),
    (CxLine $T0.AddSeconds(1) 'event_msg' '{"type":"task_started","turn_id":"turn-1","started_at":1}'),
    (CxLine $T0.AddMinutes(3) 'response_item' '{"type":"message","content":"I mention \"task_complete\" and \"task_started\" in text"}'),
    (CxLine $T0.AddMinutes(5) 'token_usage_record' '{"x":1}'),
    (CxLine $T0.AddMinutes(9) 'event_msg' '{"type":"task_complete","turn_id":"turn-1"}'),
    (CxLine $T0.AddMinutes(20) 'event_msg' '{"type":"task_started","turn_id":"turn-2"}'),
    (CxLine $T0.AddMinutes(26) 'event_msg' '{"type":"item_completed","turn_id":"turn-2"}')
)
[IO.File]::WriteAllText($main, (($lines -join "`n") + "`n"))
$guard = Join-Path $cxd 'rollout-2026-11-10T09-00-01-thread-guard.jsonl'
[IO.File]::WriteAllText($guard, ((@((CxLine $T0 'session_meta' '{"id":"thread-guard","cwd":"c:\\x\\Toah_Nipi_Booking_Software","source":{"subagent":{"other":"guardian"}},"thread_source":"guardian_review"}'), (CxLine $T0.AddMinutes(1) 'event_msg' '{"type":"task_started","turn_id":"g-1"}'), (CxLine $T0.AddMinutes(2) 'event_msg' '{"type":"task_complete","turn_id":"g-1"}')) -join "`n") + "`n"))
[IO.File]::WriteAllText((Join-Path $cx 'session_index.jsonl'), '{"id":"thread-main","thread_name":"Fix the booking calendar","updated_at":"x"}' + "`n")
$cxlog = New-Object ClaudeStatus.ActivityLog((Join-Path $tmp 'cx-activity'))
$watch = New-Object ClaudeStatus.CodexWatch($cx, $cxlog, (Join-Path $tmp 'cx-activity\codex-index.json'))
$n1 = $watch.Poll($T0.AddHours(1))
$cxe = $cxlog.Read($T0.AddDays(-1), $T0.AddDays(1))
Check 'Codex: turns become start/done events; text that merely mentions them does not' ((($cxe | Where-Object { $_.E -ne 'tick' } | ForEach-Object { $_.E }) -join ',') -eq 'start,done,start' -and @($cxe | Where-Object { $_.S -eq 'thread-main' }).Count -eq $cxe.Count) (($cxe | ForEach-Object { $_.E + '@' + $_.S }) -join ',')
Check 'Codex: reviewer / sub-agent threads are skipped (they run inside a parent turn)' (@($cxe | Where-Object { $_.S -eq 'thread-guard' }).Count -eq 0)
Check 'Codex: folder name only, like Claude''s' (@($cxe | Where-Object { $_.Pj -ne 'Toah_Nipi_Booking_Software' }).Count -eq 0)
$n2 = $watch.Poll($T0.AddHours(1))
Check 'Codex: polling again reads nothing new (offsets saved)' ($n2 -eq 0 -and $cxlog.Read($T0.AddDays(-1), $T0.AddDays(1)).Count -eq $cxe.Count)
[IO.File]::AppendAllText($main, (CxLine $T0.AddMinutes(31) 'event_msg' '{"type":"task_complete","turn_id":"turn-2"}') + "`n")
$watch2 = New-Object ClaudeStatus.CodexWatch($cx, $cxlog, (Join-Path $tmp 'cx-activity\codex-index.json'))   # an app restart
$n3 = $watch2.Poll($T0.AddHours(1))
Check 'Codex: after a restart only the new line is read' ($n3 -eq 1)
Remove-Item (Join-Path $tmp 'cx-activity\codex-index.json')
$watch3 = New-Object ClaudeStatus.CodexWatch($cx, $cxlog, (Join-Path $tmp 'cx-activity\codex-index.json'))
$null = $watch3.Poll($T0.AddHours(1))
$src2 = New-Object ClaudeStatus.CalendarSource; $src2.Log = $cxlog; $src2.CodexHome = $cx; $src2.Zone = $pst; $src2.Now = [TCal]::At($T0.AddHours(1))
$cw = $src2.Week((New-Object DateTime(2026, 11, 10)))
Check 'Codex: its history read twice (index lost) still makes exactly two runs' ($cw.Runs.Count -eq 2 -and @($cw.Runs | Where-Object { $_.Status -eq 'completed' }).Count -eq 2) (($cw.Runs | ForEach-Object { $_.Status }) -join ',')
Check 'Codex: runs carry Codex''s own thread name; waiting is marked as not recorded' ($cw.Runs[0].Title -eq 'Fix the booking calendar' -and -not $cw.Runs[0].WaitsKnown -and $cw.Runs[0].Provider -eq 'codex')
$open = Fold (EvList (E 'cx1' 'start' 0 -p 'codex') (E 'cx1' 'tick' 4 -p 'codex')) $T0.AddMinutes(60)
Check 'Codex: a turn that never logged completion ends at its last log line, marked not recorded' ($open[0].Status -eq 'lost' -and $open[0].End -eq $T0.AddMinutes(4))
$mixed = Fold (EvList (E 'k1' 'start' 0) (E 'k1' 'done' 5) (E 'k2' 'start' 0 -p 'codex') (E 'k2' 'done' 5 -p 'codex')) $T0.AddHours(1)
$on = Build $mixed $null $T0.AddHours(1) $true; $off = Build $mixed $null $T0.AddHours(1) $false
Check 'Include Codex on: both providers; off: Codex hidden (and said so)' ($on.Runs.Count -eq 2 -and $off.Runs.Count -eq 1 -and $off.Runs[0].Provider -eq 'claude' -and (($CAL::Notes($off, $pst)) -join ' ') -match 'Codex activity is hidden')
$cfgOff = New-Object 'System.Collections.Generic.Dictionary[string,object]'; $cfgOff['includeCodex'] = $false
$src2.Config = [TCal]::Cfg($cfgOff)
Check 'turning it off deletes nothing: the stored Codex events are all still there' ($src2.Week((New-Object DateTime(2026, 11, 10))).Runs.Count -eq 0 -and $cxlog.Read($T0.AddDays(-1), $T0.AddDays(1)).Count -ge $cxe.Count)
Check 'labels say which AI: "Claude" / "Codex"' (($CAL::BlockLine(($on.Blocks | Where-Object { $_.Run.Provider -eq 'codex' } | Select-Object -First 1), $pst, $T0)) -like 'Codex ·*' -and ($CAL::BlockLine(($on.Blocks | Where-Object { $_.Run.Provider -eq 'claude' } | Select-Object -First 1), $pst, $T0)) -like 'Claude ·*')

# ======================================================================================
Section 'Calendar: phone API, desktop rendering and performance'
# ======================================================================================
# a busy fixture week: 90 days of history, ~25 prompts a day across projects, overlaps, waits, overnight runs
$perfDir = Join-Path $tmp 'cal-perf'
$plog = New-Object ClaudeStatus.ActivityLog($perfDir)
$rng = New-Object Random 7
$projects = 'Toah_Nipi_Website', 'Toah_Nipi_Booking_Software', 'Toah_Nipi_Portal', 'Guest_Booking_Form', 'Velo', 'ChessPal', 'AI-Status-Indicator'
$batch = New-Object 'System.Collections.Generic.List[ClaudeStatus.ActEvent]'
$pNow = New-Object DateTime(2026, 11, 13, 22, 0, 0, [DateTimeKind]::Utc)
for ($d = 90; $d -ge 0; $d--) {
    for ($k = 0; $k -lt 25; $k++) {
        $s = "perf-$d-$($k % 4)"; $st = $pNow.AddDays(-$d).Date.AddMinutes($rng.Next(0, 1440)); $len = [Math]::Pow($rng.NextDouble(), 2) * 90
        $pj = $projects[$rng.Next($projects.Count)]; $id = "pf$d-$k"
        $ev = @(@{ e = 'start'; t = $st }, @{ e = 'tick'; t = $st.AddMinutes($len * 0.3) })
        if ($k % 3 -eq 0) { $ev += @{ e = 'wait'; t = $st.AddMinutes($len * 0.4); d = 'Bash' }; $ev += @{ e = 'resume'; t = $st.AddMinutes($len * 0.6) } }
        if ($k % 11 -ne 5) { $ev += @{ e = 'done'; t = $st.AddMinutes($len) } }
        $i = 0; foreach ($x in $ev) { $i++; $o = New-Object ClaudeStatus.ActEvent; $o.Id = "$id-$i"; $o.P = $(if ($k % 5 -eq 0) { 'codex' } else { 'claude' }); $o.S = $s; $o.E = $x.e; $o.T = $x.t; $o.Pj = $pj; $o.D = [string]$x.d; $batch.Add($o) }
    }
}
$plog.Append($batch)
$psrc = New-Object ClaudeStatus.CalendarSource; $psrc.Log = $plog; $psrc.Zone = $pst; $psrc.Now = [TCal]::At($pNow)
$qd = New-Object ClaudeStatus.QueueData; $tp = Task 'future' 'Scheduled' $pNow.AddHours(3); $qd.Tasks.Add($tp)
$psrc.Queue = QData($qd)
$sw = [Diagnostics.Stopwatch]::StartNew(); $pw = $psrc.Week((New-Object DateTime(2026, 11, 13))); $first = $sw.ElapsedMilliseconds
$sw.Restart(); 1..10 | ForEach-Object { $null = $psrc.Week((New-Object DateTime(2026, 11, 13))) }; $again = $sw.ElapsedMilliseconds / 10
Info "90 days, $($batch.Count) events: first week read $first ms, then $again ms per refresh; $($pw.Blocks.Count) blocks this week"
Check 'a week out of 90 busy days builds quickly (< 1.5 s cold, < 250 ms per refresh)' ($first -lt 1500 -and $again -lt 250) "cold=$first warm=$again"
$overlap = 0
foreach ($day in 0..6) {
    $bs = @($pw.Blocks | Where-Object { $_.Day -eq $day })
    for ($a = 0; $a -lt $bs.Count; $a++) { for ($b = $a + 1; $b -lt $bs.Count; $b++) {
        $x = $bs[$a]; $y = $bs[$b]
        $vx = [Math]::Max($x.EndMin, $x.StartMin + 12); $vy = [Math]::Max($y.EndMin, $y.StartMin + 12)
        $sameCol = ($x.Col / $x.Cols) -lt (($y.Col + 1) / $y.Cols) -and ($y.Col / $y.Cols) -lt (($x.Col + 1) / $x.Cols)
        if ($sameCol -and $x.StartMin -lt $vy -and $y.StartMin -lt $vx) { $overlap++ } } }
}
Check 'no two drawn blocks cover each other anywhere in that busy week' ($overlap -eq 0) "$overlap overlaps"

$view = New-Object ClaudeStatus.CalendarView($psrc, 1.0)
$view.Size = New-Object Drawing.Size(1100, 700); $null = $view.Handle; $view.CreateControl()
$view.ShownDate = New-Object DateTime(2026, 11, 13)
$sw.Restart(); $bmp = New-Object Drawing.Bitmap(1100, 700); $view.DrawToBitmap($bmp, (New-Object Drawing.Rectangle(0, 0, 1100, 700))); $paint = $sw.ElapsedMilliseconds
Check 'the desktop week paints quickly with a busy week (< 500 ms)' ($paint -lt 500) "$paint ms"
$bmp.Save((Join-Path $RenderDir 'calendar-desktop-busy.png')); $bmp.Dispose(); $view.Dispose()

# --- the phone API: same week, display data only ---
$cp = [THttp]::FreePort()
$croot = New-PhoneRoot $cp
$ps = New-Object ClaudeStatus.PhoneServer($croot, $cp, $null, $null, [Func[string[]]] { @() })
$ps.Calendar = $psrc
$null = $ps.Start(); $ps.Publish((New-Object 'System.Collections.Generic.List[ClaudeStatus.SessionInfo]'), $false, '')
try {
    $noHdr = Req $cp 'GET' '/api/calendar'
    Check 'phone API: refused without the page''s header (cross-site reads)' ($noHdr.Code -eq 403)
    $ok = Req $cp 'GET' '/api/calendar?d=2026-11-13' @{ 'X-Claude-Status' = 'calendar'; 'Sec-Fetch-Site' = 'same-origin' }
    $j = $ok.Body | ConvertFrom-Json
    Check 'phone API: the same week the desktop builds (days, blocks, columns)' ($ok.Code -eq 200 -and $j.days.Count -eq 7 -and $j.blocks.Count -eq $pw.Blocks.Count -and $j.days[0] -eq $pw.Days[0].ToString('yyyy-MM-dd')) "code=$($ok.Code) blocks=$($j.blocks.Count)/$($pw.Blocks.Count)"
    Check 'phone API: no session ids or task ids, only tokens' ($ok.Body -notmatch 'perf-\d' -and $ok.Body -notmatch '"future"' -and @($j.blocks | Where-Object { $_.k -notmatch '^[A-Za-z0-9_-]{22}$' }).Count -eq 0)
    $bad = Req $cp 'GET' '/api/calendar?d=../../etc' @{ 'X-Claude-Status' = 'calendar' }
    Check 'phone API: a malformed date is refused' ($bad.Code -eq 400)
    $post = Req $cp 'POST' '/api/calendar' @{ 'X-Claude-Status' = 'calendar' } '{}'
    Check 'phone API: read-only (POST refused)' ($post.Code -eq 405)
} finally { $ps.Stop() }

# --- fixture renders for a visual check: overlaps, waiting, overnight, short, planned, uncertain ending ---
$fixDir = Join-Path $tmp 'cal-fixture'
$flog = New-Object ClaudeStatus.ActivityLog($fixDir)
$fNow = (New-Object DateTime(2026, 11, 12, 22, 40, 0, [DateTimeKind]::Utc))   # Thu 2:40 PM Pacific
function FE([string]$s, [string]$e, [DateTime]$t, [string]$pj, [string]$p = 'claude', [string]$d = '') { $o = New-Object ClaudeStatus.ActEvent; $script:evn++; $o.Id = "fx$($script:evn)"; $o.P = $p; $o.S = $s; $o.E = $e; $o.T = $t; $o.Pj = $pj; $o.D = $d; $o }
$day = { param($d, $h, $m) (New-Object DateTime(2026, 11, $d, $h, $m, 0, [DateTimeKind]::Unspecified)) }
function PT($d, $h, $m) { [TimeZoneInfo]::ConvertTimeToUtc((New-Object DateTime(2026, 11, $d, $h, $m, 0)), $pst) }
$fx = New-Object 'System.Collections.Generic.List[ClaudeStatus.ActEvent]'
foreach ($x in @(
    (FE 'w1' 'start' (PT 9 9 5) 'Toah_Nipi_Website'), (FE 'w1' 'wait' (PT 9 9 40) 'Toah_Nipi_Website' 'claude' 'Bash'), (FE 'w1' 'resume' (PT 9 10 5) 'Toah_Nipi_Website'), (FE 'w1' 'done' (PT 9 11 10) 'Toah_Nipi_Website'),
    (FE 'v1' 'start' (PT 9 9 30) 'Velo'), (FE 'v1' 'done' (PT 9 10 20) 'Velo'),
    (FE 'c1' 'start' (PT 9 10 0) 'Toah_Nipi_Booking_Software' 'codex'), (FE 'c1' 'done' (PT 9 10 45) 'Toah_Nipi_Booking_Software' 'codex'),
    (FE 's1' 'start' (PT 10 13 0) 'ChessPal'), (FE 's1' 'done' (PT 10 13 1) 'ChessPal'),
    (FE 'n1' 'start' (PT 10 22 50) 'Toah_Nipi_Portal'), (FE 'n1' 'wait' (PT 11 0 30) 'Toah_Nipi_Portal' 'claude' 'AskUserQuestion'), (FE 'n1' 'resume' (PT 11 0 55) 'Toah_Nipi_Portal'), (FE 'n1' 'wait' (PT 11 1 30) 'Toah_Nipi_Portal'), (FE 'n1' 'resume' (PT 11 1 45) 'Toah_Nipi_Portal'), (FE 'n1' 'done' (PT 11 2 20) 'Toah_Nipi_Portal'),
    (FE 'l1' 'start' (PT 11 15 0) 'Guest_Booking_Form'), (FE 'l1' 'tick' (PT 11 15 35) 'Guest_Booking_Form'),
    (FE 'r1' 'start' (PT 12 13 10) 'Toah_Nipi_Booking_Software'), (FE 'r1' 'wait' (PT 12 14 20) 'Toah_Nipi_Booking_Software' 'claude' 'permission_prompt'),
    (FE 'r2' 'start' (PT 12 13 50) 'AI-Status-Indicator'), (FE 'r2' 'tick' (PT 12 14 30) 'AI-Status-Indicator')
)) { $fx.Add($x) }
$flog.Append($fx)
$fq = New-Object ClaudeStatus.QueueData
$fq.Tasks.Add((Task 'p1' 'Scheduled' (PT 13 2 0))); $fq.Tasks[0].Title = 'Overnight dependency audit'
$t2 = Task 'p2' 'Scheduled' (PT 12 17 30); $t2.Title = 'Refresh guest form copy'; $t2.Project = 'C:\work\Velo'; $fq.Tasks.Add($t2)
$t3 = Task 'p3' 'Canceled' (PT 11 7 0) 'missed'; $t3.Title = 'Morning report'; $fq.Tasks.Add($t3)
$fsrc = New-Object ClaudeStatus.CalendarSource; $fsrc.Log = $flog; $fsrc.Zone = $pst; $fsrc.Now = [TCal]::At($fNow); $fsrc.Queue = QData($fq)
$fsrc.ClaudeLive = [TCal]::Live(@('r1', 'r2'))
$aliases = New-Object 'System.Collections.Generic.Dictionary[string,object]'; $aliases['Toah_Nipi_Website'] = 'Website'; $aliases['Toah_Nipi_Booking_Software'] = 'Booking Software'; $aliases['Toah_Nipi_Portal'] = 'Portal'; $aliases['Guest_Booking_Form'] = 'Guest Form'
$fcfg = New-Object 'System.Collections.Generic.Dictionary[string,object]'; $fcfg['projectAliases'] = $aliases.PSObject.BaseObject; $fcfg['projectCategories'] = $ov.PSObject.BaseObject
Check 'fixture: aliases and categories reach the calendar as the app reads them' ($CAL::Category('Guest_Booking_Form', $fcfg['projectCategories']) -eq 'toahnipi' -and $fcfg['projectAliases'] -is [System.Collections.Generic.Dictionary[string,object]])
$fsrc.Config = [TCal]::Cfg($fcfg)
$fw = $fsrc.Week((New-Object DateTime(2026, 11, 12)))
Check 'fixture week: running and waiting blocks are live, the lost one is marked, all seven kinds present' (@($fw.Runs | Where-Object { $_.Status -eq 'waiting' }).Count -eq 1 -and @($fw.Runs | Where-Object { $_.Status -eq 'running' }).Count -eq 1 -and @($fw.Runs | Where-Object { $_.Status -eq 'lost' }).Count -eq 1 -and $fw.Plans.Count -eq 3) (($fw.Runs | ForEach-Object { $_.Session + '=' + $_.Status }) -join ',')
foreach ($sc in 1.0, 1.5) {
    $v = New-Object ClaudeStatus.CalendarView($fsrc, $sc)
    $sz = New-Object Drawing.Size([int](1000 * $sc), [int](640 * $sc)); $v.Size = $sz; $null = $v.Handle; $v.CreateControl()
    $v.ShownDate = New-Object DateTime(2026, 11, 12)
    if ($sc -eq 1.0) { $sel = @($v.Week.Blocks | Where-Object { $_.Run -and $_.Run.Session -eq 'n1' })[0]; $v.GetType().GetMethod('SelectBlock', [Reflection.BindingFlags]'NonPublic,Instance').Invoke($v, @([ClaudeStatus.CalendarView]::Key($sel))) }
    $b2 = New-Object Drawing.Bitmap($sz.Width, $sz.Height); $v.DrawToBitmap($b2, (New-Object Drawing.Rectangle(0, 0, $sz.Width, $sz.Height)))
    $b2.Save((Join-Path $RenderDir ("calendar-desktop-{0}.png" -f [int]($sc * 100)))); $b2.Dispose(); $v.Dispose()
}
Info "Calendar renders saved to $RenderDir (calendar-desktop-*.png)"
$script:calFixture = $fsrc   # the -Browser checks show this same week on the phone

# --- the phone page in a real browser (run-tests.ps1 -Browser): Day / Week layout, taps, details, screenshots ---
$cChrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$cNode = Get-Command node -ErrorAction SilentlyContinue
if (-not $Browser) { Skip 'calendar on the phone page in a browser' 'run with -Browser' }
elseif (-not $cChrome -or -not $cNode) { Skip 'calendar on the phone page in a browser' 'needs Chrome or Edge, and Node.js 22+' }
else {
    $bp = [THttp]::FreePort()
    $broot = New-PhoneRoot $bp
    $bs = New-Object ClaudeStatus.PhoneServer($broot, $bp, $null, $null, [Func[string[]]] { @() })
    $bs.Calendar = $fsrc
    $null = $bs.Start(); $bs.Publish((New-Object 'System.Collections.Generic.List[ClaudeStatus.SessionInfo]'), $false, '')
    $bprof = Join-Path $tmp 'chrome-cal'
    $bc = Start-Process $cChrome -ArgumentList '--headless=new', '--remote-debugging-port=0', "--user-data-dir=`"$bprof`"", '--no-first-run', '--no-default-browser-check', '--disable-extensions', 'about:blank' -PassThru
    try {
        $pf = Join-Path $bprof 'DevToolsActivePort'
        for ($i = 0; $i -lt 100 -and -not (Test-Path $pf); $i++) { Start-Sleep -Milliseconds 100 }
        $dbgPort = if (Test-Path $pf) { (Get-Content $pf)[0] } else { '' }
        if (-not $dbgPort) { Check 'calendar browser: headless browser started' $false }
        else {
            $outF = Join-Path $tmp 'cal-browser.txt'
            $np = Start-Process $cNode.Source -ArgumentList "`"$(Join-Path $PSScriptRoot 'calendar-browser.mjs')`"", $dbgPort, $bp, "`"$RenderDir`"" -PassThru -NoNewWindow -RedirectStandardOutput $outF -RedirectStandardError (Join-Path $tmp 'cal-browser-err.txt')
            $deadline = (Get-Date).AddMinutes(3)
            while (-not $np.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
            if (-not $np.HasExited) { $np.Kill(); Check 'calendar browser: finished in time' $false }
            $lines = @(Get-Content $outF -ErrorAction SilentlyContinue | Where-Object { $_ -match '^(PASS|FAIL)\t' })
            if (-not $lines.Count) { Check 'calendar browser: produced results' $false ((Get-Content (Join-Path $tmp 'cal-browser-err.txt') -Raw -ErrorAction SilentlyContinue) -as [string]) }
            foreach ($l in $lines) { $p = $l -split "`t"; Check ('phone calendar: ' + $p[1]) ($p[0] -eq 'PASS') $p[2] }
            Info "Phone calendar screenshots: $RenderDir\phone-cal-*.png"
        }
    } finally { try { Stop-Process -Id $bc.Id -Force -ErrorAction SilentlyContinue } catch {}; $bs.Stop() }
}

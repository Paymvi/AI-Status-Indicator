# Prompt scheduler tests (dot-sourced by run-tests.ps1 after common.ps1). Everything runs against temporary folders
# and tests\mock-claude.ps1 instead of Claude Code: no real Claude call, no usage, nothing outside $tmp is touched.

Section 'Scheduler rules (pure): time zones and DST, reset times, cycles, arguments, outcomes'
$S = [ClaudeStatus.Sched]
$pst = [TimeZoneInfo]::FindSystemTimeZoneById('Pacific Standard Time')
$gap = $S::LocalToUtc((New-Object DateTime(2026, 3, 8, 2, 30, 0)), $pst)
Check 'DST spring-forward gap (2:30 doesn''t exist) -> the first valid time after it (3:00 PDT)' ($gap -eq (New-Object DateTime(2026, 3, 8, 10, 0, 0, [DateTimeKind]::Utc))) "$gap"
$amb = $S::LocalToUtc((New-Object DateTime(2026, 11, 1, 1, 30, 0)), $pst)
Check 'DST fall-back (1:30 happens twice) -> the first occurrence (PDT)' ($amb -eq (New-Object DateTime(2026, 11, 1, 8, 30, 0, [DateTimeKind]::Utc))) "$amb"
$norm = $S::LocalToUtc((New-Object DateTime(2026, 7, 1, 9, 0, 0)), $pst)
Check 'ordinary summer time converts with the summer offset' ($norm -eq (New-Object DateTime(2026, 7, 1, 16, 0, 0, [DateTimeKind]::Utc))) "$norm"
$base = New-Object DateTime(2026, 11, 9, 14, 0, 0)   # a Monday, 2 PM
Check 'reset text "resets 3:45pm" -> today 15:45' (($S::ParseResetText("You've hit your session limit · resets 3:45pm", $base)) -eq $base.Date.AddHours(15).AddMinutes(45))
Check 'reset text "resets 9am" (already past) -> tomorrow 09:00' (($S::ParseResetText('limit · resets 9am', $base)) -eq $base.Date.AddDays(1).AddHours(9))
Check 'reset text "resets Mon 12:00am" -> next Monday 00:00' (($S::ParseResetText('weekly limit · resets Mon 12:00am', $base)) -eq $base.Date.AddDays(7))
Check 'reset text "resets 3pm (Europe/London)" is read as local 15:00' (($S::ParseResetText('resets 3pm (Europe/London)', $base)) -eq $base.Date.AddHours(15))
Check 'no reset text -> unknown (never guessed)' ($null -eq $S::ParseResetText("You've hit your usage limit", $base))

$mk = { param($id, $after) $t = New-Object ClaudeStatus.QueueTask; $t.Id = $id; if ($after) { $t.Schedule = 'after'; $t.After = $after }; $t }
$chain = New-Object 'System.Collections.Generic.List[ClaudeStatus.QueueTask]'
$chain.Add((& $mk 'a' $null)); $chain.Add((& $mk 'b' 'a')); $chain.Add((& $mk 'c' 'b'))
Check 'cycle check: a -> after c (c after b after a) is a loop' ($S::CreatesCycle($chain, 'a', 'c'))
Check 'cycle check: a task can''t wait for itself' ($S::CreatesCycle($chain, 'a', 'a'))
Check 'cycle check: a new task after c is fine' (-not $S::CreatesCycle($chain, 'd', 'c'))

$E = [ClaudeStatus.QueueEngine]
Check 'argument quoting: spaces, quotes, trailing backslashes' ($E::QuoteArg('C:\My Folder\') -eq '"C:\My Folder\\"' -and $E::QuoteArg('a"b') -eq '"a\"b"' -and $E::QuoteArg('plain') -eq 'plain' -and $E::QuoteArg('') -eq '""')
$err = $null
$cl = $E::CommandLine(@('C:\npm\claude.cmd'), [Collections.Generic.List[string]]@('-p', '--settings', 'C:\x&y\s.json'), [ref]$err)
Check 'a .cmd install refuses arguments with cmd.exe metacharacters (never a shell injection)' ($null -eq $cl -and $err -match 'safely')
$cl = $E::CommandLine(@('C:\Program Files\claude.exe'), [Collections.Generic.List[string]]@('-p', '--resume', '0b6f7a2e-1111-2222-3333-444455556666'), [ref]$err)
Check 'command line: exe quoted, flags as-is, no prompt text' ($cl -eq '"C:\Program Files\claude.exe" -p --resume 0b6f7a2e-1111-2222-3333-444455556666') $cl

function RepOf([string[]]$lines) { $r = New-Object ClaudeStatus.RunReport; foreach ($l in $lines) { [ClaudeStatus.RunParser]::Feed($r, $l) }; $r }
$now = [DateTime]::UtcNow
$ok = RepOf @('{"type":"system","subtype":"init","apiKeySource":"none","session_id":"s"}', '{"type":"result","subtype":"success","is_error":false,"result":"Fixed it.","num_turns":3,"duration_ms":2000,"permission_denials":[]}')
$v = [ClaudeStatus.Outcome]::Classify($ok, 0, '', $false, $false, $now)
Check 'outcome: success result -> completed, worded as Claude''s unverified report' ($v.Kind -eq 'completed' -and $v.Message -match 'independently verified')
$v = [ClaudeStatus.Outcome]::Classify((RepOf @()), 0, '', $false, $false, $now)
Check 'outcome: exit code 0 without a result is NOT success (interrupted / uncertain)' ($v.Kind -eq 'interrupted')
$v = [ClaudeStatus.Outcome]::Classify((RepOf @()), 1, 'boom', $false, $false, $now)
Check 'outcome: non-zero exit without a result -> failed' ($v.Kind -eq 'failed')
$reset = [DateTimeOffset]::UtcNow.AddHours(2).ToUnixTimeSeconds()
$v = [ClaudeStatus.Outcome]::Classify((RepOf @("{`"type`":`"rate_limit_event`",`"rate_limit_info`":{`"status`":`"rejected`",`"resetsAt`":$reset,`"rateLimitType`":`"five_hour`"}}", '{"type":"result","subtype":"success","is_error":true,"result":"You''ve hit your session limit · resets 3:45pm"}')), 1, '', $false, $false, $now)
Check 'outcome: rate_limit_event rejected -> usage limit with the reported reset (epoch seconds)' ($v.Kind -eq 'usage_limit' -and $v.ResetReported -and [Math]::Abs(($v.ResetUtc - [DateTimeOffset]::FromUnixTimeSeconds($reset).UtcDateTime).TotalSeconds) -lt 1)
$v = [ClaudeStatus.Outcome]::Classify((RepOf @('{"type":"result","subtype":"success","is_error":true,"result":"API Error: Server is temporarily limiting requests (not your usage limit)"}')), 1, '', $false, $false, $now)
Check 'outcome: "temporarily limiting (not your usage limit)" is a service problem, not a usage limit' ($v.Kind -eq 'network')
$v = [ClaudeStatus.Outcome]::Classify((RepOf @('{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login","api_error_status":401}')), 1, '', $false, $false, $now)
Check 'outcome: not signed in -> auth (not a usage limit or a failure)' ($v.Kind -eq 'auth')
$v = [ClaudeStatus.Outcome]::Classify((RepOf @('{"type":"result","subtype":"success","is_error":false,"result":"done","permission_denials":[{"tool_name":"Bash","tool_input":{"command":"git push"}}]}')), 0, '', $false, $false, $now)
Check 'outcome: success with blocked actions -> denied (needs attention), lists them' ($v.Kind -eq 'denied' -and $v.Message -match 'git push')
$v = [ClaudeStatus.Outcome]::Classify((RepOf @('{"type":"result","subtype":"success","is_error":true,"result":"API Error: Connection error. (fetch failed)"}')), 1, '', $false, $false, $now)
Check 'outcome: connection error -> network' ($v.Kind -eq 'network')
Check 'permission rules: deny commit/push/delete by default, both shells; allowed commands become allow rules' ($(
        $t = [ClaudeStatus.TaskEditor]::NewTask(); $al = New-Object 'Collections.Generic.List[string]'; $dn = New-Object 'Collections.Generic.List[string]'
        $S::Rules($t, $al, $dn)
        ($dn -contains 'Bash(git commit *)') -and ($dn -contains 'PowerShell(git push *)') -and ($dn -contains 'Bash(rm *)') -and ($al -contains 'Bash(npm test *)') -and ($al -contains 'PowerShell(npm test *)') -and -not ($al -match 'git (commit|push)')))
Check 'permission rules: an explicit authorization moves commit from deny to allow' ($(
        $t = [ClaudeStatus.TaskEditor]::NewTask(); $t.AllowCommit = $true; $al = New-Object 'Collections.Generic.List[string]'; $dn = New-Object 'Collections.Generic.List[string]'
        $S::Rules($t, $al, $dn); ($al -contains 'Bash(git commit *)') -and -not ($dn -contains 'Bash(git commit *)') -and ($dn -contains 'Bash(git push *)')))
Check 'allowed commands: "*" (everything) is refused' ($null -ne $S::CheckCommand('*'))

# ---------------------------------------------------------------------------------------------------------------
Section 'Scheduler with a mock Claude: runs, conversations, dependencies, limits, recovery, cancel'
$cfgDir = Join-Path $tmp 'claude-config'; $null = New-Item -ItemType Directory -Force (Join-Path $cfgDir 'projects')
$oldCfgDir = $env:CLAUDE_CONFIG_DIR; $env:CLAUDE_CONFIG_DIR = $cfgDir
$mockLog = Join-Path $tmp 'mock-log'; $null = New-Item -ItemType Directory -Force $mockLog
$env:MOCK_LOG_DIR = $mockLog
$mock = Join-Path $PSScriptRoot 'mock-claude.ps1'
$proj = Join-Path $tmp 'My Projéct 日本\app one'; $null = New-Item -ItemType Directory -Force $proj
$proj2 = Join-Path $tmp 'other project'; $null = New-Item -ItemType Directory -Force $proj2
$script:offset = [TimeSpan]::Zero
$script:active = New-Object 'System.Collections.Generic.List[ClaudeStatus.ActiveSession]'

function New-Engine([string]$root) {
    $e = New-Object ClaudeStatus.QueueEngine($root)
    $e.ClaudeCommand = @($winPs, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $mock)
    $e.ProjectsDir = Join-Path $cfgDir 'projects'
    $e.Now = [Func[DateTime]] { [DateTime]::UtcNow.Add($script:offset) }
    $e.ActiveSessionsOverride = [Func[System.Collections.Generic.List[ClaudeStatus.ActiveSession]]] { , $script:active }
    $e.Grace = [TimeSpan]::Zero
    $e.KeepAwake = $false
    $e.Start()
    return $e
}
function QTask($eng, [string]$title, [string]$project, [string]$prompt, [hashtable]$set = @{}) {
    $t = [ClaudeStatus.TaskEditor]::NewTask(); $t.Title = $title; $t.Project = $project; $t.Prompt = $prompt; $t.State = 'Scheduled'
    foreach ($k in $set.Keys) { $t.$k = $set[$k] }
    $e = $eng.Save($t, 0); if ($e) { throw "Save failed: $e" }
    return $t.Id
}
function Drive($eng, [scriptblock]$until, [int]$sec = 40) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $sec) { $eng.Tick(); if (& $until) { return $true }; Start-Sleep -Milliseconds 150 }
    return $false
}
function Q($eng, [string]$id) { $eng.Data.Find($id) }
function Settled($eng, [string]$id) { $t = Q $eng $id; $t.State -ne 'Running' -and $t.State -ne 'Queued' -and -not $eng.HasRun }
function Calls { if (Test-Path (Join-Path $mockLog 'calls.jsonl')) { @(Get-Content (Join-Path $mockLog 'calls.jsonl') -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }
function Clear-Calls { Remove-Item (Join-Path $mockLog 'calls.jsonl') -ErrorAction SilentlyContinue }
function ArgAfter($call, [string]$flag) { $a = @($call.args); $i = [Array]::IndexOf($a, $flag); if ($i -ge 0) { $a[$i + 1] } else { $null } }

$qroot = New-Root
$eng = New-Engine $qroot
$notes = New-Object System.Collections.ArrayList
$eng.add_Notify([Action[ClaudeStatus.QueueTask]] { param($t) [void]$notes.Add($t.Title + '|' + $t.State) })

# 1. new conversation, exact prompt bytes, Windows paths, no API key, permission file
$env:ANTHROPIC_API_KEY = 'sk-test-not-a-real-key'
$prompt1 = "Line 1: fix the tests`r`nLine 2 — Unicode: héllo 日本 ✓`r`n`r`n    indented ""quotes"" & | > %PATH% `$env:X`r`n"
Clear-Calls
$id1 = QTask $eng 'First task' $proj $prompt1
$done = Drive $eng { Settled $eng $id1 }
Remove-Item Env:ANTHROPIC_API_KEY
$t1 = Q $eng $id1
$c = @(Calls)[0]
Check 'new task runs and completes (Claude''s success result)' ($done -and $t1.State -eq 'Completed' -and $t1.Outcome -eq 'completed') "$($t1.State) $($t1.Reason)"
Check 'new conversation: --session-id with a fresh UUID, kept on the task for follow-ups' ([ClaudeStatus.Sched]::IsGuid($t1.SessionId) -and (ArgAfter $c '--session-id') -eq $t1.SessionId -and -not (@($c.args) -contains '--resume') -and -not (@($c.args) -contains '--continue'))
Check 'headless flags: -p, stream-json, --permission-prompts none, a --settings file; never bypassPermissions' ((@($c.args) -contains '-p') -and (ArgAfter $c '--output-format') -eq 'stream-json' -and (ArgAfter $c '--permission-prompts') -eq 'none' -and (ArgAfter $c '--permission-mode') -eq 'acceptEdits' -and -not (@($c.args) -match 'bypass|dangerously'))
$sentBytes = [Convert]::FromBase64String($c.prompt)
Check 'prompt arrives on stdin byte-for-byte (multiline, Unicode, quotes, shell characters)' ([Text.Encoding]::UTF8.GetString($sentBytes) -eq $prompt1)
Check 'prompt text never appears on the command line' (-not (@($c.args) -join ' ').Contains('fix the tests'))
Check 'runs in the project folder (spaces + Unicode in the path)' ($c.cwd -eq $proj) $c.cwd
Check 'ANTHROPIC_API_KEY is removed from Claude''s environment (no silent switch to API billing)' (-not $c.apikey)
$setFile = ArgAfter $c '--settings'
$perm = (Get-Content $setFile -Raw | ConvertFrom-Json).permissions
Check 'settings file: deny rules for commit/push/delete, allow rules for the dev commands' ((@($perm.deny) -contains 'Bash(git push *)') -and (@($perm.allow) -contains 'Bash(npm test *)'))
Check 'summary and Claude''s final message are kept' ($t1.Summary -eq 'All done.' -and $t1.ResultText -match 'tests pass')
$outText = $eng.OutputOf($t1, -1)
Check 'readable output: Claude''s text, tool calls and the result line' ($outText -match 'Looking at the project' -and $outText -match '• Bash: npm test' -and $outText -match '■ Result: success')
Check 'the run''s log, settings and history are kept locally; the prompt file is removed after the run' ((Test-Path $eng.LogPath($id1, 1)) -and -not (Test-Path (Join-Path $eng.RunsDir "$id1\1.prompt.txt")))
Check 'notification: one "Completed" for the task' (@($notes | Where-Object { $_ -eq 'First task|Completed' }).Count -eq 1)

# 2. conversations: follow-up after the predecessor, resuming a chosen one, safety checks
$eng.SetPaused($true)
$idA = QTask $eng 'Step A' $proj 'step A'
$idB = QTask $eng 'Step B (continues A)' $proj 'step B' @{ Schedule = 'after'; After = $idA; Conversation = 'predecessor' }
Check 'paused queue: nothing starts' (-not $eng.HasRun -and (Q $eng $idA).State -eq 'Queued' -and (Q $eng $idB).State -eq 'Scheduled' -and (Q $eng $idB).Reason -match 'Waits for "Step A"')
Clear-Calls
$eng.SetPaused($false)
$done = Drive $eng { (Settled $eng $idA) -and (Settled $eng $idB) -and (Q $eng $idB).State -ne 'Scheduled' }
$a = Q $eng $idA; $b = Q $eng $idB; $calls = @(Calls)
Check 'dependency order: B starts only after A finished successfully' ($done -and $a.State -eq 'Completed' -and $b.State -eq 'Completed' -and $b.Last.StartedUtc -ge $a.Last.EndedUtc) "done=$done A=$($a.State) $($a.Last.EndedUtc.ToString('o')) B=$($b.State) $($b.Last.StartedUtc.ToString('o')) $($b.Reason)"
Check 'B resumes A''s conversation by ID (--resume <A''s session>), with B''s own prompt' ((ArgAfter $calls[1] '--resume') -eq $a.SessionId -and $b.SessionId -eq $a.SessionId -and [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($calls[1].prompt)) -eq 'step B')

Clear-Calls
$idR = QTask $eng 'Resume chosen' $proj 'continue please' @{ Conversation = 'resume'; ResumeSession = $t1.SessionId }
$null = Drive $eng { Settled $eng $idR }
Check 'continue an existing conversation: --resume with exactly that session' ((Q $eng $idR).State -eq 'Completed' -and (ArgAfter @(Calls)[0] '--resume') -eq $t1.SessionId)
$idX = QTask $eng 'Wrong folder' $proj2 'x' @{ Conversation = 'resume'; ResumeSession = $t1.SessionId }
$null = Drive $eng { Settled $eng $idX } 5
Check 'a conversation from another project folder is not resumed: Needs attention, with the reason' ((Q $eng $idX).State -eq 'NeedsAttention' -and (Q $eng $idX).Reason -match 'belongs to another folder')
$idM = QTask $eng 'Missing conversation' $proj 'x' @{ Conversation = 'resume'; ResumeSession = [Guid]::NewGuid().ToString() }
$null = Drive $eng { Settled $eng $idM } 5
Check 'a conversation that doesn''t exist: Needs attention ("wasn''t found")' ((Q $eng $idM).State -eq 'NeedsAttention' -and (Q $eng $idM).Reason -match "wasn't found")
$s = New-Object ClaudeStatus.ActiveSession; $s.SessionId = $t1.SessionId; $s.Cwd = $proj; $s.Status = 'idle'; $s.Kind = 'interactive'; $s.Pid = 4242; $script:active.Add($s)
$idO = QTask $eng 'Open elsewhere' $proj 'x' @{ Conversation = 'resume'; ResumeSession = $t1.SessionId }
$null = Drive $eng { Settled $eng $idO } 5
Check 'a conversation open in another Claude Code session is not resumed: Needs attention' ((Q $eng $idO).State -eq 'NeedsAttention' -and (Q $eng $idO).Reason -match 'open in another Claude Code session')
$script:active.Clear()
$idF = QTask $eng 'Fails' $proj 'MOCK:fail'
$idD = QTask $eng 'Depends on failing' $proj 'never runs' @{ Schedule = 'after'; After = $idF }
$null = Drive $eng { (Settled $eng $idF) -and (Q $eng $idD).State -eq 'Blocked' }
Check 'a failed predecessor blocks its dependent, with the reason' ((Q $eng $idF).State -eq 'Failed' -and (Q $eng $idD).State -eq 'Blocked' -and (Q $eng $idD).Reason -match 'Fails" was failed' -and (Q $eng $idD).Attempts.Count -eq 0)
$cyc = [ClaudeStatus.TaskEditor]::NewTask(); $cyc.Id = $idF; $cyc.Title = 'Fails'; $cyc.Project = $proj; $cyc.Prompt = 'x'; $cyc.Schedule = 'after'; $cyc.After = $idD; $cyc.State = 'Scheduled'
Check 'saving a dependency loop is refused' ($eng.Save($cyc, (Q $eng $idF).Revision) -match 'loop')
$eng.MarkCompleted($idF)
$null = Drive $eng { Settled $eng $idD }
Check 'marking the predecessor completed unblocks the dependent' ((Q $eng $idD).State -eq 'Completed')

# 3. usage limits: midway (continue the same conversation, don't resend the prompt), global waiting, no reset time
Clear-Calls
$env:MOCK_RESET_SEC = '1800'
$eng.SetPaused($true)
$idU = QTask $eng 'Hits the limit' $proj 'MOCK:usage-midway do the big refactor'
$idW = QTask $eng 'Waits behind it' $proj2 'small thing'
$eng.SetPaused($false)
$null = Drive $eng { (Q $eng $idU).State -eq 'WaitingUsage' -and -not $eng.HasRun }
$u = Q $eng $idU; $w = Q $eng $idW
Check 'usage limit midway: Waiting for usage, retry right after the reported reset (+1 min)' ($u.State -eq 'WaitingUsage' -and $u.ResetSource -eq 'reported' -and [Math]::Abs(($u.NextRetryUtc - [DateTime]::UtcNow.AddSeconds(1860)).TotalSeconds) -lt 60) "$($u.State) $($u.NextRetryUtc) $($u.Reason)"
Check 'it will continue the same conversation (session kept)' ($u.NeedsContinue -and $u.Reason -match 'continues the same conversation')
Check 'the other queued task also waits for the usage reset (no launch into a known limit)' ($w.State -eq 'WaitingUsage' -and $w.Attempts.Count -eq 0 -and $w.Reason -match 'resets')
Check 'one notification per waiting condition' (@($notes | Where-Object { $_ -eq 'Hits the limit|WaitingUsage' }).Count -eq 1)
for ($k = 0; $k -lt 5; $k++) { $eng.Tick() }
Check 'repeated ticks while waiting launch nothing and don''t notify again' (@(Calls).Count -eq 1 -and @($notes | Where-Object { $_ -like 'Hits the limit|*' }).Count -eq 1)
$script:offset = [TimeSpan]::FromMinutes(32)
$null = Drive $eng { (Settled $eng $idU) -and (Settled $eng $idW) -and (Q $eng $idW).State -eq 'Completed' }
$u = Q $eng $idU; $calls = @(Calls)
$second = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($calls[1].prompt))
Check 'after the reset it resumes the same conversation (--resume) instead of starting over' ($u.State -eq 'Completed' -and (ArgAfter $calls[1] '--resume') -eq $u.SessionId -and $u.Attempts.Count -eq 2)
Check 'the continuation prompt asks to continue; the original prompt is not resent' ($second -match '^Continue the task' -and $second -notmatch 'big refactor' -and $second -match 'usage limit')
Check 'then the waiting task runs' ((Q $eng $idW).State -eq 'Completed')
$script:offset = [TimeSpan]::Zero
$eng2 = New-Engine (New-Root)   # fresh engine: no remembered limit
$idN = QTask $eng2 'No reset time' $proj 'MOCK:usage-noreset'
$null = Drive $eng2 { (Q $eng2 $idN).State -eq 'WaitingUsage' -and -not $eng2.HasRun }
$n = Q $eng2 $idN
Check 'usage limit without a reset time: bounded backoff (15 min first), marked as estimated' ($n.ResetSource -eq 'backoff' -and [Math]::Abs(($n.NextRetryUtc - [DateTime]::UtcNow.AddMinutes(15)).TotalSeconds) -lt 60 -and $n.Reason -match 'estimated')
$mine = [DateTime]::UtcNow.AddMinutes(5)
$eng2.SetRetryTime($idN, $mine)
Check 'the next try time can be edited' ((Q $eng2 $idN).NextRetryUtc -eq $mine -and (Q $eng2 $idN).ResetSource -eq 'you')
$eng2.Cancel($idN)
$idUs = QTask $eng2 'When usage is back' $proj 'later' @{ Schedule = 'usage'; UsageResetUtc = [DateTime]::UtcNow.AddHours(1) }
$eng2.Tick()
Check 'schedule "when usage is available" with a reset time you supply waits for it' ((Q $eng2 $idUs).State -eq 'WaitingUsage' -and (Q $eng2 $idUs).Reason -match 'you said')
$eng2.Cancel($idUs)
$eng2.UsageBlockedUntil = [Func[DateTime]] { [DateTime]::UtcNow.AddHours(2) }
$idUb = QTask $eng2 'Usage shows 100%' $proj 'x'
$eng2.Tick()
Check 'a limit Claude Usage reports as 100% used delays new runs until its reset' ((Q $eng2 $idUb).State -eq 'WaitingUsage' -and (Q $eng2 $idUb).Attempts.Count -eq 0)
$eng2.RunNow($idUb)
$null = Drive $eng2 { Settled $eng2 $idUb }
Check 'Run now overrides the usage wait (your explicit choice)' ((Q $eng2 $idUb).State -eq 'Completed')
$eng2.UsageBlockedUntil = $null

# 4. other failure kinds are told apart
$idP = QTask $eng2 'Blocked action' $proj 'MOCK:deny'
$null = Drive $eng2 { Settled $eng2 $idP }
$p = Q $eng2 $idP
Check 'permission block: Needs attention (not Completed), denied action recorded, session kept to take over' ($p.State -eq 'NeedsAttention' -and $p.Outcome -eq 'denied' -and ($p.Denials -join ';') -match 'git push' -and [ClaudeStatus.Sched]::IsGuid($p.SessionId))
$idAu = QTask $eng2 'Signed out' $proj 'MOCK:auth'
$null = Drive $eng2 { Settled $eng2 $idAu }
Check 'auth error: Needs attention with sign-in advice (not retried as a usage limit)' ((Q $eng2 $idAu).State -eq 'NeedsAttention' -and (Q $eng2 $idAu).Outcome -eq 'auth' -and (Q $eng2 $idAu).Reason -match '/login')
$idNe = QTask $eng2 'Offline' $proj 'MOCK:network'
$null = Drive $eng2 { (Q $eng2 $idNe).Outcome -eq 'network' -and -not $eng2.HasRun }
$ne = Q $eng2 $idNe
Check 'network problem: retried later with backoff, continuing its conversation' ($ne.State -eq 'Scheduled' -and $ne.NextRetryUtc -gt [DateTime]::UtcNow -and $ne.NeedsContinue -and $ne.Reason -match 'Retrying')
$eng2.Cancel($idNe)
$idCr = QTask $eng2 'Crashes' $proj 'MOCK:crash'
$null = Drive $eng2 { Settled $eng2 $idCr }
Check 'Claude exiting with an error code and no result: Failed, with the exit code' ((Q $eng2 $idCr).State -eq 'Failed' -and (Q $eng2 $idCr).Last.ExitCode -eq 3)
$idK = QTask $eng2 'API key' $proj 'MOCK:apikey'
$sw = [Diagnostics.Stopwatch]::StartNew()
$null = Drive $eng2 { Settled $eng2 $idK } 15
Check 'a run that would bill an API key is stopped at once: Needs attention' ((Q $eng2 $idK).State -eq 'NeedsAttention' -and (Q $eng2 $idK).Outcome -eq 'api_key' -and $sw.Elapsed.TotalSeconds -lt 12)

# 5. busy project, duplicate launches
$s = New-Object ClaudeStatus.ActiveSession; $s.SessionId = [Guid]::NewGuid().ToString(); $s.Cwd = $proj.ToUpperInvariant() + '\'; $s.Status = 'busy'; $s.Kind = 'interactive'; $s.Pid = 777; $script:active.Add($s)
$idBz = QTask $eng2 'Busy project' $proj 'x'
for ($k = 0; $k -lt 3; $k++) { $eng2.Tick() }
Check 'manual Claude activity in the same folder defers the task (same path, other case/slash)' ((Q $eng2 $idBz).State -eq 'Queued' -and (Q $eng2 $idBz).Reason -match 'busy in this project' -and (Q $eng2 $idBz).Attempts.Count -eq 0)
$s.Status = 'idle'
$null = Drive $eng2 { Settled $eng2 $idBz }
Check 'when that session goes idle the task runs' ((Q $eng2 $idBz).State -eq 'Completed')
$script:active.Clear()

$droot = New-Root
$e1 = New-Engine $droot; $e2 = New-Engine $droot
$e1.SetPaused($true)
$idDup = QTask $e1 'Only once' $proj 'MOCK:sleep=2'
Clear-Calls
$e1.SetPaused($false)
for ($k = 0; $k -lt 6; $k++) { $e1.Tick(); $e2.Tick(); $e2.NoteWake(); $e2.Tick() }
$null = Drive $e1 { Settled $e1 $idDup }
for ($k = 0; $k -lt 3; $k++) { $e2.Tick() }
Check 'two app instances + repeated timer and wake events launch the task exactly once' (@(Calls).Count -eq 1 -and (Q $e1 $idDup).Attempts.Count -eq 1 -and (Q $e1 $idDup).State -eq 'Completed') "calls=$(@(Calls).Count)"
$e2.Dispose()

# 6. recovery: quit while running (reattach), crash (uncertain -> needs attention, never rerun)
$rroot = New-Root
$er = New-Engine $rroot
$idRe = QTask $er 'Survives a restart' $proj 'MOCK:sleep=4'
$null = Drive $er { $er.HasRun } 10
$pidRun = $er.RunningPid
$er.Dispose()   # like quitting Claude Status: Claude keeps running
Start-Sleep -Milliseconds 300
Check 'quitting the app leaves the running Claude process alone' ($null -ne (Get-Process -Id $pidRun -ErrorAction SilentlyContinue))
$er2 = New-Engine $rroot
$er2.Tick()
Check 'after a restart the running task is picked up again (not relaunched)' ($er2.HasRun -and (Q $er2 $idRe).State -eq 'Running' -and (Q $er2 $idRe).Reason -match 'picked up again')
$null = Drive $er2 { Settled $er2 $idRe }
$re = Q $er2 $idRe
Check '... and finishes from its log (one attempt; exit code read from the reattached process)' ($re.State -eq 'Completed' -and $re.Attempts.Count -eq 1 -and $re.Last.ExitCode -eq 0) "$($re.State) $($re.Attempts.Count) $($re.Last.ExitCode)"
$idCrash = QTask $er2 'Interrupted' $proj 'MOCK:sleep=60'
$null = Drive $er2 { $er2.HasRun -and $er2.LiveOutput($idCrash) -match 'working on it' } 15
$pidRun = $er2.RunningPid
$er2.AbandonForTest()
Stop-Process -Id $pidRun -Force; Start-Sleep -Milliseconds 300
$null = $er2.Store.Mutate([Func[ClaudeStatus.QueueData, bool]] { param($d) $d.Find($idCrash).Last.OwnerPid = 999999; $true })   # its app "crashed"
$er3 = New-Engine $rroot
for ($k = 0; $k -lt 3; $k++) { $er3.Tick() }
$ic = Q $er3 $idCrash
Check 'crash while running + Claude gone without a result: Needs attention (uncertain), not rerun automatically' ($ic.State -eq 'NeedsAttention' -and $ic.Outcome -eq 'interrupted' -and $ic.Attempts.Count -eq 1 -and -not $er3.HasRun)
Clear-Calls
$er3.Retry($idCrash, $true)
$null = Drive $er3 { @(Calls).Count -ge 1 } 15
$rc = @(Calls)[0]
Check 'Continue conversation (your explicit choice) resumes it with a continuation prompt' ((Q $er3 $idCrash).Attempts.Count -eq 2 -and (ArgAfter $rc '--resume') -eq (Q $er3 $idCrash).SessionId -and [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($rc.prompt)) -match '^Continue the task') "attempts=$((Q $er3 $idCrash).Attempts.Count) state=$((Q $er3 $idCrash).State) reason=$((Q $er3 $idCrash).Reason) calls=$(@(Calls).Count) args=$($rc.args -join ' ')"
$er3.Cancel($idCrash); $null = Drive $er3 { -not $er3.HasRun } 20

# 7. cancel: the whole process tree, partial output kept
$croot = New-Root
$ec = New-Engine $croot
Remove-Item (Join-Path $mockLog 'child.txt') -ErrorAction SilentlyContinue
$idC = QTask $ec 'Cancel me' $proj 'MOCK:child'
$null = Drive $ec { Test-Path (Join-Path $mockLog 'child.txt') } 20
Start-Sleep -Milliseconds 500; $ec.Tick()
$childPid = [int](Get-Content (Join-Path $mockLog 'child.txt'))
$mainPid = $ec.RunningPid
$ec.Cancel($idC)
Start-Sleep -Milliseconds 500
$cc = Q $ec $idC
Check 'cancel stops Claude and the processes it started (job object)' ($null -eq (Get-Process -Id $childPid -ErrorAction SilentlyContinue) -and $null -eq (Get-Process -Id $mainPid -ErrorAction SilentlyContinue))
Check 'canceled: recorded as Canceled, says edits are not undone, partial output kept' ($cc.State -eq 'Canceled' -and $cc.Reason -match 'not undone' -and $ec.OutputOf($cc, -1) -match 'working with a helper process')

# 8. missed schedules, overdue grace, skip, sleep/wake clock jump
$mroot = New-Root
$em = New-Engine $mroot
$em.Grace = [TimeSpan]::FromMinutes(1); $em.Start()
$em.SetPaused($true)
$idLate = QTask $em 'Missed (run)' $proj 'x' @{ Schedule = 'at'; RunAtUtc = [DateTime]::UtcNow.AddHours(-3) }
$idSkip = QTask $em 'Missed (skip)' $proj 'x' @{ Schedule = 'at'; RunAtUtc = [DateTime]::UtcNow.AddHours(-3); IfMissed = 'skip' }
$idFut = QTask $em 'Future' $proj 'x' @{ Schedule = 'at'; RunAtUtc = [DateTime]::UtcNow.AddHours(5) }
$em.SetPaused($false)
Check 'a missed task waits a minute after start-up (so you can skip it), and says so' ((Q $em $idLate).State -eq 'Scheduled' -and (Q $em $idLate).Reason -match 'Overdue.*unless you skip')
Check '"if missed: skip" tasks are skipped with the reason' ((Q $em $idSkip).State -eq 'Canceled' -and (Q $em $idSkip).Reason -match 'Skipped: it was due')
Check 'a future task is Scheduled with its local time' ((Q $em $idFut).State -eq 'Scheduled' -and (Q $em $idFut).Reason -match '^Starts ')
$script:offset = [TimeSpan]::FromSeconds(90)
$null = Drive $em { Settled $em $idLate }
Check 'after the grace minute the overdue task runs (one at a time)' ((Q $em $idLate).State -eq 'Completed')
$script:offset = [TimeSpan]::FromHours(6)   # wall clock jumps 6 h: like waking from sleep
$em.Tick()
Check 'a clock jump (sleep/wake or clock change) is noticed: the now-overdue task waits its grace minute again' ((Q $em $idFut).State -eq 'Scheduled' -and (Q $em $idFut).Reason -match 'Overdue')
$em.Skip($idFut)
Check 'Skip on an overdue task: it never runs' ((Q $em $idFut).State -eq 'Canceled' -and (Q $em $idFut).Attempts.Count -eq 0)
$script:offset = [TimeSpan]::Zero

# 9. persistence, editing guards, history
$pe = New-Engine $mroot
Check 'tasks, runs and sessions persist across restarts (new engine, same file)' ((Q $pe $idLate).State -eq 'Completed' -and (Q $pe $idLate).Attempts.Count -eq 1 -and [ClaudeStatus.Sched]::IsGuid((Q $pe $idLate).SessionId))
$draft = [ClaudeStatus.TaskEditor]::NewTask(); $draft.Title = 'Just a draft'; $draft.Project = $proj; $draft.Prompt = 'x'; $draft.State = 'Draft'
$null = $pe.Save($draft, 0); for ($k = 0; $k -lt 3; $k++) { $pe.Tick() }
$reread = (New-Engine $mroot).Data.Find($draft.Id)
Check 'unset times stay unset after saving and reading back (no time-zone shifted "year 1" dates)' ($reread.RunAtUtc -eq [DateTime]::MinValue -and $reread.NextRetryUtc -eq [DateTime]::MinValue -and $reread.FinishedUtc -eq [DateTime]::MinValue -and $reread.CreatedUtc.Kind -eq 'Utc' -and $pe.Duplicate($draft.Id).NextRetryUtc -eq [DateTime]::MinValue)
Check 'drafts never run' ((Q $pe $draft.Id).State -eq 'Draft' -and (Q $pe $draft.Id).Attempts.Count -eq 0 -and -not $pe.HasRun)
$stale = Q $pe $draft.Id; $stale.Prompt = 'edited'
Check 'saving over a newer version is refused (e.g. it started while you were editing)' ($pe.Save($stale, 99) -match 'changed while you were editing')
$idRun = QTask $pe 'Running edit' $proj 'MOCK:sleep=3'
$null = Drive $pe { $pe.HasRun } 10
$rt = Q $pe $idRun; $rt.Prompt = 'CHANGED'
$saveErr = $pe.Save($rt, $rt.Revision)
Check 'a running task''s prompt can''t be changed; the submitted prompt is kept on the run' ($saveErr -match 'running' -and (Q $pe $idRun).Last.Prompt -eq 'MOCK:sleep=3')
$null = Drive $pe { Settled $pe $idRun }
$dupe = $pe.Duplicate($idRun)
Check 'duplicate makes a draft copy without history or conversation' ($dupe.State -eq 'Draft' -and $dupe.Attempts.Count -eq 0 -and $dupe.SessionId -eq '' -and $dupe.Prompt -eq 'MOCK:sleep=3')
$before = @($pe.Data.Tasks | ForEach-Object { $_.Id })
$pe.Move($dupe.Id, -1)
$after = @($pe.Data.Tasks | ForEach-Object { $_.Id })
Check 'reorder moves a task up one place' ([Array]::IndexOf($after, $dupe.Id) -eq [Array]::IndexOf($before, $dupe.Id) - 1)
$finished = @($pe.Data.Tasks | Where-Object { [ClaudeStatus.QState]::Finished($_.State) }).Count
$cleared = $pe.ClearHistory()
Check 'Clear history deletes finished tasks and their saved output' ($cleared -eq $finished -and -not (Test-Path (Join-Path $pe.RunsDir $idRun)) -and $null -eq (Q $pe $idRun) -and $null -ne (Q $pe $draft.Id))
[IO.File]::WriteAllText((Join-Path $mroot 'state\queue\tasks.json'), '{ broken json')
$bad = New-Engine $mroot
Check 'an unreadable tasks.json is kept (renamed), never silently lost' ($bad.Store.Warning -match 'kept as' -and @(Get-ChildItem (Join-Path $mroot 'state\queue') -Filter 'tasks.json.unreadable-*').Count -eq 1)

# 10. conversation discovery
$list = [ClaudeStatus.Sessions]::List((Join-Path $cfgDir 'projects'), $proj, 40)
Check 'conversation list: this folder''s conversations, newest first, with first prompt and where they ran' ($list.Count -ge 2 -and $list[0].LastUtc -ge $list[1].LastUtc -and ($list | Where-Object { $_.Id -eq $t1.SessionId }).FirstPrompt -match 'Line 1: fix the tests' -and $list[0].Where -eq 'scheduled / -p')
Check 'conversation list for another folder doesn''t show these' (@([ClaudeStatus.Sessions]::List((Join-Path $cfgDir 'projects'), $proj2, 40) | Where-Object { $_.Id -eq $t1.SessionId }).Count -eq 0)

# ---------------------------------------------------------------------------------------------------------------
Section 'Status / Queue views, settings migration and scheduled workers on the board'
$vf = Form (New-Root)
$vf.Home = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Location + (New-Object System.Drawing.Size(200, 200))
$statusBounds = $vf.Bounds
$vf.ShowQueue($true)
$wa = [System.Windows.Forms.Screen]::FromRectangle($vf.Bounds).WorkingArea
Check 'Queue view: a larger window, within the monitor''s working area' ($vf.IsQueue -and $vf.Width -gt $statusBounds.Width * 2 -and $wa.Contains($vf.Bounds) -and $null -ne $vf.QueueViewControl) "$($vf.Bounds)"
Check 'Queue header: toggle / settings / minimize / close; "Switch to Status"' ($vf.HitAt((New-Object System.Drawing.Point(($vf.HeaderButtons[0].Left + 5), ($vf.HeaderButtons[0].Top + 5)))) -eq 'toggle' -and $vf.TooltipFor('toggle') -eq 'Switch to Status' -and $vf.HitAt((New-Object System.Drawing.Point(40, 8))) -eq 'drag')
$vf.ShowQueue($false)
Check 'switching back restores the Status window exactly' ($vf.IsBoard -and $vf.Bounds -eq $statusBounds) "$statusBounds -> $($vf.Bounds)"
Check 'Status tooltip: "Switch to Queue"' ($vf.TooltipFor('toggle') -like 'Switch to Queue*')
$vq = $vf.Queue
$vq.ClaudeCommand = @($winPs, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $mock); $vq.ProjectsDir = Join-Path $cfgDir 'projects'; $vq.ActiveSessionsOverride = [Func[System.Collections.Generic.List[ClaudeStatus.ActiveSession]]] { , $script:active }
$idV = QTask $vq 'Across views' $proj 'MOCK:sleep=3'
$null = Drive $vq { $vq.HasRun } 10
$vf.ShowQueue($true); $vf.ShowQueue($false); $vf.ShowQueue($true)
Check 'switching views doesn''t pause or interrupt a running task' ($vq.HasRun -and (Q $vq $idV).State -eq 'Running')
$null = Drive $vq { Settled $vq $idV }
Check '... which then completes normally' ((Q $vq $idV).State -eq 'Completed')
$vf.ShowQueue($false)
$vsid = (Q $vq $idV).SessionId
Put $vf.GetType().GetField('root', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($vf) $vsid 'DONE' (Split-Path $proj -Leaf) 0 1
$vf.ReloadNow()
Check 'a scheduled run is one worker row (from its own hooks), named after its task' ($vf.Workers.Count -eq 1 -and $vf.Workers[0].Name -eq '⏱ Across views')
Check 'its tooltip says it''s a scheduled task and opens in the Queue' ($vf.TooltipFor('row:' + $vsid) -match 'Scheduled task: Across views' -and $vf.TooltipFor('row:' + $vsid) -match 'open it in the Queue')
$vf.Dispose()

$mr = New-Root @{ startupView = 'compact' }
$f = Form $mr
Check 'old startupView=compact: opens in Status and the setting is migrated to workerBoard' ($f.IsBoard -and (Get-Content (Join-Path $mr 'config.json') -Raw | ConvertFrom-Json).startupView -eq 'workerBoard')
$f.Dispose()
$f = Form (New-Root @{ startupView = 'queue' }); Check 'startupView=queue opens in Queue' $f.IsQueue; $f.Dispose()

if ($null -ne $oldCfgDir) { $env:CLAUDE_CONFIG_DIR = $oldCfgDir } else { Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue }
Remove-Item Env:MOCK_LOG_DIR, Env:MOCK_RESET_SEC -ErrorAction SilentlyContinue
foreach ($x in @($eng, $eng2, $e1, $er2, $er3, $ec, $em, $pe, $bad)) { try { $x.Dispose() } catch { } }

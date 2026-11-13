# Scheduling from the phone (POST/GET /api/queue in phone.cs) against the real desktop app (IndicatorForm, test mode)
# and its real QueueEngine, with tests\mock-claude.ps1 instead of Claude Code: temporary folders only, no real Claude
# call, no usage. Dot-sourced by run-tests.ps1 (after the phone dashboard section, whose HTTP helpers it uses), or alone:
#   powershell -ExecutionPolicy Bypass -File .\tests\phone-schedule-tests.ps1
if (-not (Get-Command Check -ErrorAction SilentlyContinue)) {
    if ($PSVersionTable.PSEdition -eq 'Core') { & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath; exit $LASTEXITCODE }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('claude-status-psched-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    . (Join-Path $PSScriptRoot 'common.ps1')
    $pschedStandalone = $true
}
if (-not ('THttp' -as [type])) {
    Add-Type @'
using System; using System.IO; using System.Text; using System.Threading.Tasks; using System.Net.Sockets;
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
    public static int FreePort() { var l = new TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
}
'@
}

Section 'Phone scheduling: create, edit, cancel, stop, duplicates, validation, request protection, execution'

$sp = [THttp]::FreePort()
$sroot = New-Root @{ phoneDashboard = $true; phonePort = $sp }
foreach ($fn in 'phone.html', 'phone.css', 'phone.js', 'sw.js', 'workers.png', 'favicon.ico', 'worker-192.png', 'apple-touch-icon.png') { Copy-Item (Join-Path $src $fn) $sroot }
# Claude Code's own folders, faked: one recent conversation in a project (so it is a "known project"), and the mock.
$scfg = Join-Path $tmp 'psched-claude'; $sproj = Join-Path $scfg 'projects'
$portal = Join-Path $tmp 'work\Toah_Nipi_Portal'; $other = Join-Path $tmp 'work\Side Project ü'; $gone = Join-Path $tmp 'work\Deleted_Project'
foreach ($d in $portal, $other) { $null = New-Item -ItemType Directory -Force $d }
function FakeTranscript([string]$cwd, [string]$sid) {
    $dir = Join-Path $sproj ([ClaudeStatus.Sessions]::Mangle($cwd)); $null = New-Item -ItemType Directory -Force $dir
    [IO.File]::WriteAllText((Join-Path $dir "$sid.jsonl"), ((@{ type = 'user'; cwd = $cwd; sessionId = $sid } | ConvertTo-Json -Compress) + "`n"))
}
FakeTranscript $portal ([Guid]::NewGuid().ToString())
FakeTranscript $other ([Guid]::NewGuid().ToString())
FakeTranscript $gone ([Guid]::NewGuid().ToString())   # its folder doesn't exist: never offered
$oldCfg = $env:CLAUDE_CONFIG_DIR; $env:CLAUDE_CONFIG_DIR = $scfg
$smock = Join-Path $tmp 'psched-mock'; $null = New-Item -ItemType Directory -Force $smock
$oldMock = $env:MOCK_LOG_DIR; $env:MOCK_LOG_DIR = $smock

$sf = Form $sroot
$null = $sf.Handle   # the UI thread the server hands scheduler actions to (pumped below while waiting)
$sq = $sf.Queue
$sq.ClaudeCommand = @($winPs, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'mock-claude.ps1'))
$sq.ProjectsDir = $sproj; $sq.KeepAwake = $false; $sq.Grace = [TimeSpan]::Zero
$sq.ActiveSessionsOverride = [Func[System.Collections.Generic.List[ClaudeStatus.ActiveSession]]] { , (New-Object 'System.Collections.Generic.List[ClaudeStatus.ActiveSession]') }
$script:shift = [TimeSpan]::Zero
$sq.Now = [Func[DateTime]] { [DateTime]::UtcNow.Add($script:shift) }
$sf.Phone.ClaudeProjectsDir = $sproj
$sf.ReloadNow()

function SPump($t) { while (-not $t.IsCompleted) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 5 } }
function SRaw([string]$method, [string]$path, [hashtable]$h, [string]$body) {
    $lines = @("$method $path HTTP/1.1", "Host: 127.0.0.1:$sp")
    foreach ($k in $h.Keys) { if ($null -ne $h[$k]) { $lines += "${k}: $($h[$k])" } }
    if ($null -ne $body) { $lines += "Content-Length: $([Text.Encoding]::UTF8.GetByteCount($body))" }
    (($lines -join "`r`n") + "`r`n`r`n" + $body)
}
function SH([hashtable]$override = @{}) {
    $h = @{ Origin = "http://127.0.0.1:$sp"; 'Content-Type' = 'application/json'; 'X-Claude-Status' = 'queue'; 'Sec-Fetch-Site' = 'same-origin' }
    foreach ($k in $override.Keys) { $h[$k] = $override[$k] }; $h
}
function SAns([string]$raw) { $j = $null; try { $j = [THttp]::Body($raw) | ConvertFrom-Json } catch { }; @{ Code = [THttp]::Status($raw); J = $j; Body = [THttp]::Body($raw) } }
function SReq([string]$method, [string]$path, [hashtable]$h, [string]$body = $null) { $t = [THttp]::SendAsync($sp, (SRaw $method $path $h $body), 30000); SPump $t; SAns $t.Result }
function SPost($body, [hashtable]$override = @{}) { SReq 'POST' '/api/queue' (SH $override) ($(if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Compress -Depth 5 })) }
function SBoard { (SReq 'GET' '/api/board' @{}).J }
function SId { $b = New-Object byte[] 16; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
$sEpoch = New-Object DateTime(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
function SMs([DateTime]$utc) { [long]($utc - $sEpoch).TotalMilliseconds }
function SCalls { $p = Join-Path $smock 'calls.jsonl'; if (Test-Path $p) { @(Get-Content $p -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }

# ---- the board says scheduling is available, with the laptop's time zone ----
$b = SBoard
Check 'board: the queue says the phone can schedule, with the laptop''s time zone and offset' ($b.queue.can -eq $true -and $b.queue.tz -ne '' -and $b.queue.tzo -eq [int][TimeZoneInfo]::Local.GetUtcOffset([DateTime]::Now).TotalMinutes) ($b.queue | ConvertTo-Json -Compress -Depth 2)

# ---- projects: friendly names + tokens, never paths ----
$pr = SReq 'GET' '/api/queue?op=projects' @{ 'X-Claude-Status' = 'queue' }
$names = @($pr.J.projects | ForEach-Object { $_.n })
Check 'projects: Claude Code''s recent folders, by friendly name (alias), existing folders only' ($pr.Code -eq 200 -and $names -contains 'Portal' -and $names -contains 'Side Project ü' -and $names -notcontains 'Deleted Project') ($names -join '|')
Check 'projects: tokens, not paths, reach the phone' ($pr.Body -notmatch [regex]::Escape(($tmp -replace '\\', '\\')) -and $pr.Body -notmatch 'Users' -and @($pr.J.projects | Where-Object { $_.k -notmatch '^[A-Za-z0-9_-]{22}$' }).Count -eq 0)
$pk = ($pr.J.projects | Where-Object { $_.n -eq 'Portal' }).k; $pk2 = ($pr.J.projects | Where-Object { $_.n -eq 'Side Project ü' }).k
Check 'projects: listing needs the page''s own header' ((SReq 'GET' '/api/queue?op=projects' @{}).Code -eq 403 -and (SReq 'GET' '/api/queue?op=projects' @{ 'X-Claude-Status' = 'git' }).Code -eq 403 -and (SReq 'GET' '/api/queue?op=projects' @{ 'X-Claude-Status' = 'queue'; 'Sec-Fetch-Site' = 'cross-site' }).Code -eq 403)

# ---- request protection: no write without the dashboard's own origin, header and JSON ----
$okBody = @{ op = 'save'; id = (SId); pj = $pk; p = 'never saved'; at = (SMs ([DateTime]::UtcNow.AddHours(3))); rn = 'vscode'; pm = 'acceptEdits'; md = ''; im = 'run'; ai = 'claude' }
$prot = @(
    (SPost $okBody @{ Origin = $null }).Code, (SPost $okBody @{ Origin = 'http://evil.example' }).Code, (SPost $okBody @{ Origin = "http://127.0.0.1:$sp.evil.example" }).Code,
    (SPost $okBody @{ 'X-Claude-Status' = $null }).Code, (SPost $okBody @{ 'X-Claude-Status' = 'git' }).Code, (SPost $okBody @{ 'Sec-Fetch-Site' = 'cross-site' }).Code,
    (SPost $okBody @{ 'Content-Type' = 'text/plain' }).Code)
Check 'writes refused without same-origin Origin, the queue header, same-origin fetch and JSON (403/403/403/403/403/403/415)' (($prot -join ',') -eq '403,403,403,403,403,403,415') ($prot -join ',')
$m = @('GET', 'PUT', 'DELETE') | ForEach-Object { (SReq $_ '/api/queue' (SH)).Code }
Check 'other methods: GET without op is refused, PUT/DELETE not allowed' (($m -join ',') -eq '400,405,405') ($m -join ',')
$big = try { (SPost ('{"op":"save","p":"' + ('x' * 600000) + '"}')).Code } catch { 'reset' }   # refused before the body is read: 413, or the connection closes mid-upload
Check 'a body over 512 KB is refused before it is read (413 / closed)' ($big -eq 413 -or $big -eq 'reset' -or $big -eq 0) "$big"
Check 'nothing was saved by refused requests' ($sq.Data.Tasks.Count -eq 0)

# ---- create ----
$prompt = "Fix the flaky test.`nLine 2 — Unicode: héllo 日本 ✓`n    ""quotes"" & | > %PATH% `$env:X '; Remove-Item C:\ -Recurse # not a command"
$when = [DateTime]::UtcNow.AddMinutes(30).AddSeconds(17)
$id1 = SId
$body1 = @{ op = 'save'; id = $id1; pj = $pk; p = $prompt; ti = ''; at = (SMs $when); rn = 'headless'; pm = 'acceptEdits'; md = ''; im = 'run'; ai = 'claude' }
$r1 = SPost $body1
$t1 = $sq.Data.Tasks | Select-Object -First 1
Check 'create: answered only after the task is in the desktop scheduler (200 saved, created)' ($r1.Code -eq 200 -and $r1.J.result -eq 'saved' -and $r1.J.created -eq $true -and $sq.Data.Tasks.Count -eq 1) "$($r1.Code) $($r1.Body)"
$disk = (New-Object ClaudeStatus.QueueStore((Join-Path $sroot 'state\queue'))).Read()
Check 'create: persisted in tasks.json (survives a restart)' ($disk.Tasks.Count -eq 1 -and $disk.Tasks[0].Id -eq $t1.Id -and $disk.Tasks[0].Prompt -eq $prompt)
Check 'create: the exact prompt, the project''s full path (from its token), scheduled at that minute' ($t1.Prompt -eq $prompt -and $t1.Project -eq $portal -and $t1.Schedule -eq 'at' -and $t1.State -eq 'Scheduled' -and $t1.RunAtUtc -eq (New-Object DateTime($when.Year, $when.Month, $when.Day, $when.Hour, $when.Minute, 0, [DateTimeKind]::Utc))) "$($t1.Project) $($t1.RunAtUtc.ToString('o'))"
Check 'create: laptop wall-clock time and time zone recorded like the desktop editor' ($t1.TimeZone -eq [TimeZoneInfo]::Local.Id -and $t1.RunAtLocal -eq $t1.RunAtUtc.ToLocalTime().ToString('yyyy-MM-dd HH:mm'))
Check 'create: safe defaults (new conversation, default commands, no commit/push/delete, title from the first line)' ($t1.Conversation -eq 'new' -and -not $t1.AllowCommit -and -not $t1.AllowPush -and -not $t1.AllowDelete -and $t1.AllowCommands.Count -eq [ClaudeStatus.Sched]::DefaultCommands.Count -and $t1.Title -eq 'Fix the flaky test.' -and $t1.Runner -eq 'headless')
$row = $r1.J.task
Check 'create: the answer carries the task as the phone shows it (token, project name, time, editable, cancelable)' ($row.k -match '^[A-Za-z0-9_-]{22}$' -and $row.pj -eq 'Portal' -and $row.pk -eq $pk -and [long]$row.ra -eq (SMs $t1.RunAtUtc) -and $row.ed -eq $true -and $row.cx -eq $true -and $row.kd -eq 'scheduled') ($row | ConvertTo-Json -Compress)
[System.Windows.Forms.Application]::DoEvents()
$bq = @((SBoard).queue.tasks)
Check 'desktop and phone show the same task (the board publishes the scheduler''s own data)' ($bq.Count -eq 1 -and $bq[0].k -eq $row.k -and $bq[0].rv -eq $t1.Revision)

# ---- duplicates: repeated taps, resends, a changed resend of the same draft, simultaneous sends ----
$r1b = SPost $body1
Check 'resend of the same request: same answer, no second task' ($r1b.Code -eq 200 -and $r1b.J.task.k -eq $row.k -and $sq.Data.Tasks.Count -eq 1)
$body1c = $body1.Clone(); $body1c.p = $prompt + "`nAlso update the docs."
$r1c = SPost $body1c
Check 'the same draft sent again with changes (after "not confirmed"): updates its task, never a second one' ($r1c.Code -eq 200 -and $r1c.J.created -eq $false -and $sq.Data.Tasks.Count -eq 1 -and $sq.Data.Tasks[0].Prompt -match 'update the docs') "$($r1c.Body)"
$id2 = SId
$body2 = @{ op = 'save'; id = $id2; pj = $pk2; p = 'Second task'; at = (SMs ([DateTime]::UtcNow.AddHours(2))); rn = 'vscode'; pm = 'plan'; md = 'sonnet'; im = 'skip'; ai = 'claude' }
$raw2 = SRaw 'POST' '/api/queue' (SH) ($body2 | ConvertTo-Json -Compress)
$par = 1..5 | ForEach-Object { [THttp]::SendAsync($sp, $raw2, 30000) }
while (@($par | Where-Object { -not $_.IsCompleted }).Count) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 5 }
$codes = @($par | ForEach-Object { [THttp]::Status($_.Result) })
$keys = @($par | ForEach-Object { try { ([THttp]::Body($_.Result) | ConvertFrom-Json).task.k } catch { } } | Sort-Object -Unique)
Check '5 simultaneous identical sends: one task; each answer is saved, or "still saving" (then the same task)' ($sq.Data.Tasks.Count -eq 2 -and @($codes | Where-Object { $_ -ne 200 -and $_ -ne 503 }).Count -eq 0 -and $keys.Count -eq 1) "$($codes -join ',') $($keys -join ',')"
$t2 = $sq.Data.Tasks | Where-Object { $_.Title -eq 'Second task' }
Check 'advanced options are applied (VS Code, plan mode, model, skip if missed)' ($t2.Runner -eq 'vscode' -and $t2.PermissionMode -eq 'plan' -and $t2.Model -eq 'sonnet' -and $t2.IfMissed -eq 'skip' -and $t2.Project -eq $other)

# ---- server-side validation: nothing saved, a reason given ----
$n0 = $sq.Data.Tasks.Count
$bad = [ordered]@{
    'empty prompt' = @{ p = '   ' }
    'unknown project token' = @{ pj = 'AAAAAAAAAAAAAAAAAAAAAA' }
    'missing project' = @{ pj = $null }
    'time in the past' = @{ at = (SMs ([DateTime]::UtcNow.AddMinutes(-10))) }
    'time over a year away' = @{ at = (SMs ([DateTime]::UtcNow.AddDays(400))) }
    'no time' = @{ at = $null }
    'time as text' = @{ at = 'tomorrow' }
    'provider Codex' = @{ ai = 'codex' }
    'unknown runner (no arbitrary executables)' = @{ rn = 'C:\Windows\System32\cmd.exe' }
    'bypass permission mode' = @{ pm = 'bypassPermissions' }
    'model with shell characters' = @{ md = 'opus & calc.exe' }
    'prompt over 20,000 characters' = @{ p = ('x' * 20001) }
    'NUL in the prompt' = @{ p = "a`0b" }
}
$vfail = @()
foreach ($name in $bad.Keys) {
    $bb = @{ op = 'save'; id = (SId); pj = $pk; p = 'valid'; at = (SMs ([DateTime]::UtcNow.AddHours(1))); rn = 'vscode'; pm = 'acceptEdits'; md = ''; im = 'run'; ai = 'claude' }
    foreach ($k in $bad[$name].Keys) { if ($null -eq $bad[$name][$k]) { $bb.Remove($k) } else { $bb[$k] = $bad[$name][$k] } }
    $rr = SPost $bb
    if ($rr.Code -lt 400 -or -not $rr.J.msg -or $sq.Data.Tasks.Count -ne $n0) { $vfail += "$name=$($rr.Code)/$($rr.J.result)" }
}
Check 'server validation: bad project, prompt, time, provider, runner, mode, model are refused with a reason; nothing saved' ($vfail.Count -eq 0) ($vfail -join '; ')
$badOp = SPost @{ op = 'run'; id = (SId) }; $badId = SPost @{ op = 'save'; id = 'x' }
Check 'unknown action or malformed request id: 400' ($badOp.Code -eq 400 -and $badId.Code -eq 400)
$fixed = SPost @{ op = 'save'; id = (SId); pj = $pk; p = '   '; at = (SMs ([DateTime]::UtcNow.AddHours(1))); ai = 'claude' }
Check 'a refused request isn''t remembered: fixing it and sending again works' ($fixed.Code -eq 409)

# ---- edit / reschedule ----
$k1 = $row.k
$rev = ($sq.Data.Tasks | Where-Object { $_.Id -eq $t1.Id }).Revision
$runAt0 = ($sq.Data.Tasks | Where-Object { $_.Id -eq $t1.Id }).RunAtUtc
$e1 = SPost @{ op = 'save'; id = (SId); k = $k1; rv = $rev; pj = $pk; p = 'Edited prompt'; ti = 'Edited title'; rn = 'headless'; pm = 'acceptEdits'; md = ''; im = 'run'; ai = 'claude' }
$te = $sq.Data.Tasks | Where-Object { $_.Id -eq $t1.Id }
Check 'edit without a new time: prompt and title change, its time is kept, same task' ($e1.Code -eq 200 -and $e1.J.created -eq $false -and $te.Prompt -eq 'Edited prompt' -and $te.Title -eq 'Edited title' -and $te.RunAtUtc -eq $runAt0 -and $te.Revision -eq $rev + 1 -and $sq.Data.Tasks.Count -eq 2) "$($e1.Body)"
$new = [DateTime]::UtcNow.AddHours(5)
$e2 = SPost @{ op = 'save'; id = (SId); k = $k1; rv = $te.Revision; pj = $pk; p = 'Edited prompt'; at = (SMs $new); rn = 'headless'; pm = 'acceptEdits'; md = ''; im = 'run'; ai = 'claude' }
$te = $sq.Data.Tasks | Where-Object { $_.Id -eq $t1.Id }
Check 'reschedule: a new time from the phone' ($e2.Code -eq 200 -and [Math]::Abs(($te.RunAtUtc - $new).TotalSeconds) -lt 60)
$stale = SPost @{ op = 'save'; id = (SId); k = $k1; rv = ($te.Revision - 1); pj = $pk; p = 'Overwrite'; rn = 'headless'; pm = 'acceptEdits'; md = ''; im = 'run'; ai = 'claude' }
Check 'edit based on an older version (changed on the desktop meanwhile): refused, the current version returned' ($stale.Code -eq 409 -and $stale.J.result -eq 'changed' -and $stale.J.task.rv -eq $te.Revision -and ($sq.Data.Tasks | Where-Object { $_.Id -eq $t1.Id }).Prompt -eq 'Edited prompt')
# a desktop edit while the phone has the task open
$dt = [ClaudeStatus.QueueStore]::Clone($te); $dt.Prompt = 'Desktop edit'; $null = $sq.Save($dt, $te.Revision); [System.Windows.Forms.Application]::DoEvents()
$late = SPost @{ op = 'save'; id = (SId); k = $k1; rv = $te.Revision; pj = $pk; p = 'Phone edit'; rn = 'headless'; pm = 'acceptEdits'; md = ''; im = 'run'; ai = 'claude' }
Check 'desktop saved first: the phone''s edit is refused instead of overwriting it' ($late.J.result -eq 'changed' -and ($sq.Data.Tasks | Where-Object { $_.Id -eq $t1.Id }).Prompt -eq 'Desktop edit')
$bt = @((SBoard).queue.tasks) | Where-Object { $_.k -eq $k1 }
Check 'desktop edits reach the phone (prompt and revision)' ($bt.p -eq 'Desktop edit' -and $bt.rv -eq ($sq.Data.Tasks | Where-Object { $_.Id -eq $t1.Id }).Revision)

# ---- cancel (not started) ----
$k2 = (@((SBoard).queue.tasks) | Where-Object { $_.ti -eq 'Second task' }).k
$cid = SId
$c1 = SPost @{ op = 'cancel'; id = $cid; k = $k2 }
$tc = $sq.Data.Tasks | Where-Object { $_.Title -eq 'Second task' }
Check 'cancel a pending task: Canceled before it ran' ($c1.Code -eq 200 -and $c1.J.result -eq 'canceled' -and $tc.State -eq 'Canceled' -and $tc.Attempts.Count -eq 0 -and $tc.Reason -match 'before it ran')
$c2 = SPost @{ op = 'cancel'; id = (SId); k = $k2 }
Check 'cancel again: harmless (already canceled)' ($c2.Code -eq 200 -and $c2.J.result -eq 'canceled')
$ec = SPost @{ op = 'save'; id = (SId); k = $k2; rv = $tc.Revision; pj = $pk2; p = 'x'; rn = 'vscode'; pm = 'acceptEdits'; md = ''; im = 'run'; ai = 'claude' }
$bt2 = @((SBoard).queue.tasks) | Where-Object { $_.k -eq $k2 }
Check 'a canceled task can''t be edited from the phone, and isn''t marked editable' ($ec.Code -eq 409 -and $bt2.ed -eq $false -and $bt2.cx -eq $false)
$gk = SPost @{ op = 'cancel'; id = (SId); k = 'BBBBBBBBBBBBBBBBBBBBBB' }
Check 'unknown task token: refused as gone' ($gk.Code -eq 409 -and $gk.J.result -eq 'gone')

# ---- runs once, in the right project, with the exact prompt (mock Claude; the engine's clock moved forward) ----
$id3 = SId
$p3 = "Harmless test prompt: say hello.`nÜnïcödé & ""quotes"""
$r3 = SPost @{ op = 'save'; id = $id3; pj = $pk2; p = $p3; at = (SMs ([DateTime]::UtcNow.AddMinutes(30))); rn = 'headless'; pm = 'plan'; md = ''; im = 'run'; ai = 'claude' }
$k3 = $r3.J.task.k
$t3 = $sq.Data.Tasks | Where-Object { $_.Prompt -eq $p3 }
$sq.Tick()
Check 'not due yet: nothing runs' (@(SCalls).Count -eq 0 -and $t3.State -eq 'Scheduled')
$script:shift = [TimeSpan]::FromMinutes(31)
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 40) { $sq.Tick(); [System.Windows.Forms.Application]::DoEvents(); $x = $sq.Data.Find($t3.Id); if ($x.State -ne 'Scheduled' -and $x.State -ne 'Queued' -and $x.State -ne 'Running') { break }; Start-Sleep -Milliseconds 150 }
foreach ($i in 1..5) { $sq.Tick(); Start-Sleep -Milliseconds 50 }
$x = $sq.Data.Find($t3.Id); $calls = @(SCalls | Where-Object { $_.args -contains '-p' })
Check 'due: it runs exactly once, in that project, with the prompt byte for byte, and completes' ($calls.Count -eq 1 -and $calls[0].cwd -eq $other -and [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($calls[0].prompt)) -eq $p3 -and $x.State -eq 'Completed' -and $x.Attempts.Count -eq 1) "$($calls.Count) $($x.State) $($x.Reason)"
$r3b = SPost @{ op = 'save'; id = $id3; pj = $pk2; p = $p3; at = (SMs ([DateTime]::UtcNow.AddMinutes(30))); rn = 'headless'; pm = 'plan'; md = ''; im = 'run'; ai = 'claude' }
$r3c = SPost @{ op = 'save'; id = $id3; pj = $pk2; p = $p3 + ' changed'; at = (SMs ([DateTime]::UtcNow.AddMinutes(45))); rn = 'headless'; pm = 'plan'; md = ''; im = 'run'; ai = 'claude' }
$sq.Tick()
Check 'a late resend of that create (same draft, even changed) after it ran: no second task, no second run' ($r3b.Code -eq 200 -and $r3c.Code -eq 200 -and @($sq.Data.Tasks | Where-Object { $_.Prompt -like 'Harmless test prompt*' }).Count -eq 1 -and @(SCalls | Where-Object { $_.args -contains '-p' }).Count -eq 1 -and $sq.Data.Find($t3.Id).Prompt -eq $p3)
$script:shift = [TimeSpan]::Zero

# ---- stop (running) vs cancel (pending) ----
$id4 = SId
$r4 = SPost @{ op = 'save'; id = $id4; pj = $pk; p = 'Long task MOCK:sleep=60'; at = (SMs ([DateTime]::UtcNow.AddMinutes(1))); rn = 'headless'; pm = 'plan'; md = ''; im = 'run'; ai = 'claude' }
$k4 = $r4.J.task.k; $t4id = ($sq.Data.Tasks | Where-Object { $_.Prompt -eq 'Long task MOCK:sleep=60' }).Id
$script:shift = [TimeSpan]::FromMinutes(2)
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 20 -and $sq.Data.Find($t4id).State -ne 'Running') { $sq.Tick(); Start-Sleep -Milliseconds 100 }
[System.Windows.Forms.Application]::DoEvents()
$bk = @((SBoard).queue.tasks) | Where-Object { $_.k -eq $k4 }
$cr = SPost @{ op = 'cancel'; id = (SId); k = $k4 }
$er = SPost @{ op = 'save'; id = (SId); k = $k4; rv = $sq.Data.Find($t4id).Revision; pj = $pk; p = 'changed while running'; rn = 'headless'; pm = 'plan'; md = ''; im = 'run'; ai = 'claude' }
Check 'running: Cancel task and edits are refused (they never stop or change a run); not marked editable' ($sq.Data.Find($t4id).State -eq 'Running' -and $cr.J.result -eq 'running' -and $er.J.result -eq 'running' -and $bk.ed -eq $false -and $sq.Data.Find($t4id).Prompt -eq 'Long task MOCK:sleep=60') "$($cr.Body) $($er.Body)"
$st = SPost @{ op = 'stop'; id = (SId); k = $k4 }
Check 'Stop run stops it (Canceled, the run is ended)' ($st.Code -eq 200 -and $st.J.result -eq 'stopped' -and $sq.Data.Find($t4id).State -eq 'Canceled') "$($st.Body)"
$st2 = SPost @{ op = 'stop'; id = (SId); k = $k1 }
Check 'Stop run on a task that hasn''t started: refused (use Cancel task)' ($st2.Code -eq 409 -and $st2.J.result -eq 'not_running' -and $sq.Data.Find($t1.Id).State -eq 'Scheduled')
$script:shift = [TimeSpan]::Zero

# ---- the server waits for the desktop's UI thread; if it doesn't answer in time, nothing is double-done ----
# a real .NET delegate: the server calls it on its own thread, where a PowerShell script block can't run
if (-not ('TBusyUi' -as [type])) { Add-Type 'public static class TBusyUi { public static object Never(System.Func<object> work) { return null; } }' }
$sf.Phone.OnUi = [Delegate]::CreateDelegate([Func[Func[object], object]], [TBusyUi].GetMethod('Never'))   # the UI never answers (busy)
$id5 = SId
$b5 = @{ op = 'save'; id = $id5; pj = $pk; p = 'busy laptop'; at = (SMs ([DateTime]::UtcNow.AddHours(1))); rn = 'vscode'; pm = 'acceptEdits'; md = ''; im = 'run'; ai = 'claude' }
$p1 = SPost $b5; $p2 = SPost $b5
Check 'UI thread busy: "still saving" (503 pending), never "saved", and a resend doesn''t start a second save' ($p1.Code -eq 503 -and $p1.J.result -eq 'pending' -and $p2.Code -eq 503 -and $p2.J.result -eq 'pending' -and -not ($sq.Data.Tasks | Where-Object { $_.Prompt -eq 'busy laptop' }))

$sf.Dispose()
$env:CLAUDE_CONFIG_DIR = $oldCfg; $env:MOCK_LOG_DIR = $oldMock

if ($pschedStandalone) {
    Write-Host "`n$script:pass passed, $script:fail failed, $script:skip skipped" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
    try { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue } catch { }
    exit $(if ($script:fail) { 1 } else { 0 })
}

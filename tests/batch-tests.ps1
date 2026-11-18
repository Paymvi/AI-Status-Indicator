# Batches (batch.cs) against the real desktop app (IndicatorForm, test mode) and its real QueueEngine, with
# tests\mock-claude.ps1 instead of Claude Code and real Git in temporary repositories: no real Claude call, no usage,
# never this project's repository. Dot-sourced by run-tests.ps1, or run alone:
#   powershell -ExecutionPolicy Bypass -File .\tests\batch-tests.ps1 [-Browser]
param([switch]$Browser, [string]$RenderDir)
if (-not (Get-Command Check -ErrorAction SilentlyContinue)) {
    if ($PSVersionTable.PSEdition -eq 'Core') { & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Browser:$Browser; exit $LASTEXITCODE }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('claude-status-batch-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    . (Join-Path $PSScriptRoot 'common.ps1')
    $batchStandalone = $true
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

# ---------- setup: temporary repositories, the app, the mock ----------
$bt = Join-Path $tmp 'batches'; $null = New-Item -ItemType Directory -Force $bt
function BG([string]$dir) {
    $ea = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $o = & git -C $dir @args 2>&1 | ForEach-Object { "$_" }; $script:bgcode = $LASTEXITCODE; return (($o -join "`n").Trim()) }
    finally { $ErrorActionPreference = $ea }
}
function BW([string]$path, [string]$text) { $d = Split-Path $path; if (-not (Test-Path $d)) { $null = New-Item -ItemType Directory -Force $d }; [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false)) }
function New-BRepo([string]$name) {
    $d = Join-Path $bt $name; $null = New-Item -ItemType Directory -Force $d
    $null = BG $d init -q -b main
    # This temporary repository's own settings only (never your global Git configuration).
    $null = BG $d config user.name 'Batch Tester'; $null = BG $d config user.email 'batch@example.invalid'
    $null = BG $d config commit.gpgsign false; $null = BG $d config core.autocrlf false
    $null = BG $d config core.hooksPath ((Join-Path $d '.git\hooks') -replace '\\', '/')
    BW (Join-Path $d 'README.md') "# $name`n"; BW (Join-Path $d 'notes.txt') "notes`n"
    $null = BG $d add -A; $null = BG $d commit -q -m 'initial'
    return $d
}
function Count([string]$repo) { [int](BG $repo rev-list --count HEAD) }
# The last commit's message as Git stored it, read as UTF-8 (the console code page would mangle Unicode).
function GLog([string]$repo) { ([ClaudeStatus.Git]::Run($repo, [string[]]@('log', '-1', '--format=%B'), $null, 30000, $null)).Out.TrimEnd("`n") }

$bp = [THttp]::FreePort()
$broot = New-Root @{ phoneDashboard = $true; phonePort = $bp }
foreach ($fn in 'phone.html', 'phone.css', 'phone.js', 'sw.js', 'workers.png', 'favicon.ico', 'worker-192.png', 'apple-touch-icon.png') { Copy-Item (Join-Path $src $fn) $broot }
$bcfg = Join-Path $tmp 'batch-claude'; $bprojects = Join-Path $bcfg 'projects'; $null = New-Item -ItemType Directory -Force $bprojects
$bOldCfg = $env:CLAUDE_CONFIG_DIR; $env:CLAUDE_CONFIG_DIR = $bcfg
$bmock = Join-Path $tmp 'batch-mock'; $null = New-Item -ItemType Directory -Force $bmock
$bOldMock = $env:MOCK_LOG_DIR; $env:MOCK_LOG_DIR = $bmock
$bOldReset = $env:MOCK_RESET_SEC
$bf = Form $broot
$null = $bf.Handle
$bf.ReloadNow()
$bq = $bf.Queue
$bq.ClaudeCommand = @($winPs, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'mock-claude.ps1'))
$bq.ProjectsDir = $bprojects; $bq.KeepAwake = $false; $bq.Grace = [TimeSpan]::Zero
$noActive = New-Object 'System.Collections.Generic.List[ClaudeStatus.ActiveSession]'
$bq.ActiveSessionsOverride = [Func[System.Collections.Generic.List[ClaudeStatus.ActiveSession]]] { , $noActive }
$script:bnotes = New-Object System.Collections.ArrayList
$bq.add_BatchNotify([Action[ClaudeStatus.BatchRun]] { param($r) [void]$script:bnotes.Add($r.Name + '|' + $r.State + '|' + $r.PauseKind) })

$script:violations = New-Object System.Collections.ArrayList
# The batch invariants, checked on every tick: only the current step may have started; earlier steps are finished;
# no two steps of one run have unfinished tasks at once; at most one task runs at a time.
function Invariants($eng) {
    $d = $eng.Data
    $running = @($d.Tasks | Where-Object { $_.State -eq 'Running' }).Count
    if ($running -gt 1) { [void]$script:violations.Add("$running tasks running at once") }
    foreach ($r in $d.BatchRuns) {
        for ($j = 0; $j -lt $r.Steps.Count; $j++) {
            $s = $r.Steps[$j]
            if ($j -gt $r.Current -and $s.TaskId -ne '') { [void]$script:violations.Add("$($r.Name): step $($j + 1) started before step $($r.Current + 1) finished") }
            if ($j -lt $r.Current -and $s.State -ne 'done' -and $s.State -ne 'skipped' -and $s.State -ne 'canceled') { [void]$script:violations.Add("$($r.Name): step $($j + 1) is $($s.State) but the run moved on") }
        }
        $open = @($r.Steps | Where-Object { $_.TaskId -ne '' } | ForEach-Object { $d.Find($_.TaskId) } | Where-Object { $_ -and ([ClaudeStatus.QState]::Pending($_.State) -or $_.State -eq 'Running') }).Count
        if ($open -gt 1) { [void]$script:violations.Add("$($r.Name): $open steps' tasks unfinished at once") }
    }
}
function BDrive([scriptblock]$until, [int]$sec = 90, $eng = $bq) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $sec) {
        $eng.Tick(); if ($eng -eq $bq) { [System.Windows.Forms.Application]::DoEvents() }
        Invariants $eng
        if (& $until) { return $true }
        Start-Sleep -Milliseconds 150
    }
    return $false
}
function BRun([string]$id, $eng = $bq) { $eng.Data.FindRun($id) }
function Settled([string]$id, $eng = $bq) { $r = BRun $id $eng; ($r.State -eq 'Paused' -or $r.State -eq 'Completed' -or $r.State -eq 'Canceled') -and -not $eng.HasRun }
function BCalls { $p = Join-Path $bmock 'calls.jsonl'; if (Test-Path $p) { @(Get-Content $p -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }
function BClear { Remove-Item (Join-Path $bmock 'calls.jsonl') -ErrorAction SilentlyContinue }
function BArg($call, [string]$flag) { $a = @($call.args); $i = [Array]::IndexOf($a, $flag); if ($i -ge 0) { $a[$i + 1] } else { $null } }
function BPrompt($call) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($call.prompt)) }
function NewStep([hashtable]$h) { $s = New-Object ClaudeStatus.BatchStep; $s.Id = [Guid]::NewGuid().ToString('N'); foreach ($k in $h.Keys) { $s.$k = $h[$k] }; $s }
function NewBatch([string]$name, [object[]]$steps) {
    $b = New-Object ClaudeStatus.BatchDef; $b.Id = [Guid]::NewGuid().ToString('N'); $b.Name = $name
    $b.Runner = 'headless'   # the claude -p runner (tests\vscode-tests.ps1 covers the VS Code one for tasks in general)
    foreach ($c in [ClaudeStatus.Sched]::DefaultCommands) { $b.AllowCommands.Add($c) }
    foreach ($s in $steps) { $b.Steps.Add($s) }
    return $b
}
function SaveB($b) { $cur = $bq.Data.FindBatch($b.Id); $e = $bq.SaveBatch($b, $(if ($cur) { $cur.Revision } else { 0 })); if ($e) { throw "SaveBatch: $e" }; $b.Id }
function StartB([string]$id) { $rid = $null; $e = $bq.StartBatch($id, $null, [ref]$rid); if ($e) { throw "StartBatch: $e" }; $rid }
function Act([string]$rid, [string]$op, $picked = $null) { $r = BRun $rid; $l = $null; if ($picked) { $l = New-Object 'System.Collections.Generic.List[string]'; foreach ($p in $picked) { $l.Add($p) } }; $bq.BatchAction($rid, $op, $r.Seq, $l) }
function TaskOf([string]$rid, [int]$i) { $r = BRun $rid; $id = $r.Steps[$i].TaskId; if ($id) { $bq.Data.Find($id) } else { $null } }
function Pick([string]$rid, [string[]]$paths) { $s = (BRun $rid).Steps[(BRun $rid).Current]; @($s.Choices | ForEach-Object { $f = $_ -split "`t"; if ($paths -contains $f[0]) { $f[0] + "`t" + $f[1] } }) }

# phone requests (the real server of the real app, through its UI thread)
function BPump($t) { while (-not $t.IsCompleted) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 5 } }
function BRaw([string]$method, [string]$path, [hashtable]$h, [string]$body) {
    $lines = @("$method $path HTTP/1.1", "Host: 127.0.0.1:$bp")
    foreach ($k in $h.Keys) { if ($null -ne $h[$k]) { $lines += "${k}: $($h[$k])" } }
    if ($null -ne $body) { $lines += "Content-Length: $([Text.Encoding]::UTF8.GetByteCount($body))" }
    (($lines -join "`r`n") + "`r`n`r`n" + $body)
}
function BH([hashtable]$override = @{}) { $h = @{ Origin = "http://127.0.0.1:$bp"; 'Content-Type' = 'application/json'; 'X-Claude-Status' = 'queue'; 'Sec-Fetch-Site' = 'same-origin' }; foreach ($k in $override.Keys) { $h[$k] = $override[$k] }; $h }
function BReq([string]$method, [string]$path, [hashtable]$h, [string]$body = $null) {
    $t = [THttp]::SendAsync($bp, (BRaw $method $path $h $body), 30000); BPump $t
    $raw = $t.Result; $j = $null; try { $j = [THttp]::Body($raw) | ConvertFrom-Json } catch { }
    @{ Code = [THttp]::Status($raw); J = $j; Body = [THttp]::Body($raw) }
}
function BPost($body, [hashtable]$override = @{}) { BReq 'POST' '/api/queue' (BH $override) ($(if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Compress -Depth 5 })) }
function BBoard { (BReq 'GET' '/api/board' @{}).J }
function BId { $b = New-Object byte[] 16; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
function PhoneRun([string]$name) { $q = (BBoard).queue; @($q.runs | Where-Object { $_.nm -eq $name })[0] }

$repoA = New-BRepo 'repo A (main app)'
$repoB = New-BRepo 'repo-b docs'
$repoC = New-BRepo 'repo-c'
$plain = Join-Path $bt 'plain folder'; $null = New-Item -ItemType Directory -Force $plain

# ---------------------------------------------------------------------------------------------------------------
Section 'Batches: rules (validation, project switches, commit scope)'
$V = { param($b) $bq.ValidateBatch($b) }
$ok = NewBatch 'ok' @((NewStep @{ Title = 'a'; Project = $repoA; Prompt = 'x' }), (NewStep @{ Title = 'b'; Project = $repoA; Prompt = 'y'; Conversation = 'continue' }))
Check 'a valid batch: no problem' ($null -eq (& $V $ok)) (& $V $ok)
$e = & $V (NewBatch 'x' @((NewStep @{ Project = $repoA; Prompt = 'x' }), (NewStep @{ Project = $repoB; Prompt = 'y'; Conversation = 'continue' })))
Check 'continuing a conversation across projects is refused before the batch starts, and says to use a fresh conversation + handoff' ($e -match 'different project' -and $e -match 'Fresh conversation' -and $e -match 'handoff') $e
$e = & $V (NewBatch 'x' @((NewStep @{ Project = $repoA; Prompt = 'x'; Conversation = 'continue' })))
Check 'step 1 can only continue an explicitly chosen conversation' ($e -match 'Step 1' -and $e -match 'existing conversation') $e
$e = & $V (NewBatch 'x' @((NewStep @{ Project = $repoA; Prompt = 'x'; Handoff = $true })))
Check 'step 1 has no previous step to hand off from' ($e -match 'hand off') $e
$e = & $V (NewBatch 'x' @((NewStep @{ Project = $plain; Prompt = 'x'; Commit = $true; CommitMessage = 'm' })))
Check 'commit after a step needs a Git repository' ($e -match "isn't in a Git repository") $e
$e = & $V (NewBatch 'x' @((NewStep @{ Project = $repoA; Prompt = 'x'; Commit = $true; CommitMessage = '  ' })))
Check 'commit after a step needs an explicit message (no AI-written messages)' ($e -match 'commit message') $e
$e = & $V (NewBatch '' @((NewStep @{ Project = $repoA; Prompt = 'x' })))
Check 'a batch needs a name' ($e -match 'name') $e
$cb = NewBatch 'x' @((NewStep @{ Project = $repoA; Prompt = 'x' })); $cb.Agent = 'codex'
Check 'one agent per batch, and only Claude Code can run here' ((& $V $cb) -match 'Only Claude Code') (& $V $cb)

# commit scope: only files changed during the step; anything unclear is never committed by itself
$scope = New-BRepo 'scope'
BW "$scope\notes.txt" "dirty before`n"                                            # already changed before the step
BW "$scope\staged-before.txt" "x`n"; $null = BG $scope add staged-before.txt       # already staged before the step
$base = [ClaudeStatus.Git]::Inspect($scope)
$step = New-Object ClaudeStatus.BatchRunStep; $step.Commit = $true; $step.CommitMessage = 'm'; $step.Branch = $base.Branch; $step.BaseHead = $base.Oid; $step.BaselineTaken = $true
foreach ($f in $base.Files) { $step.Baseline.Add($f.Path + "`t" + $f.Hash + "`t" + $(if ($f.Staged) { '1' } else { '0' })) }
$P = [ClaudeStatus.Batching]
$plan = $P::PlanCommit([ClaudeStatus.Git]::Inspect($scope), $step, '', $null)
Check 'scope: nothing new since the step started, but a file was staged before it -> pause for picking (never committed silently)' ($plan.Kind -eq 'select' -and $plan.Note -match 'staged-before.txt') $plan.Note
$null = BG $scope reset -q -- staged-before.txt
$step.Baseline.Clear(); $base = [ClaudeStatus.Git]::Inspect($scope); foreach ($f in $base.Files) { $step.Baseline.Add($f.Path + "`t" + $f.Hash + "`t" + $(if ($f.Staged) { '1' } else { '0' })) }
$plan = $P::PlanCommit([ClaudeStatus.Git]::Inspect($scope), $step, '', $null)
Check 'scope: no new changes -> "No changes to commit"' ($plan.Kind -eq 'none' -and $plan.Note -match 'No changes to commit') $plan.Note
BW "$scope\new.txt" "new`n"
$plan = $P::PlanCommit([ClaudeStatus.Git]::Inspect($scope), $step, '', $null)
Check 'scope: a file the step created is committed; files that were already dirty and untouched are left out' ($plan.Kind -eq 'run' -and @($plan.Files).Count -eq 1 -and $plan.Files[0].Path -eq 'new.txt') (($plan.Files | ForEach-Object { $_.Path }) -join ',')
BW "$scope\notes.txt" "dirty before`nand changed again by the step`n"
$plan = $P::PlanCommit([ClaudeStatus.Git]::Inspect($scope), $step, '', $null)
Check 'scope: a file with edits from before the step that the step changed again -> pause for picking' ($plan.Kind -eq 'select' -and $plan.Note -match 'notes.txt' -and $plan.Note -match 'already had changes') $plan.Note
$null = New-Item -ItemType Directory -Force "$scope\sub"; BW "$scope\sub\in.txt" "in`n"
$plan = $P::PlanCommit([ClaudeStatus.Git]::Inspect($scope), $step, 'sub', $null)
Check 'scope: a step in a subfolder: changes outside its project folder are not attributed to it' ($plan.Kind -eq 'select' -and $plan.Note -match 'outside the step') $plan.Note
$v = [ClaudeStatus.Git]::Inspect($scope)
$picked = New-Object 'System.Collections.Generic.List[string]'; $picked.Add('new.txt' + "`t" + ($v.Files | Where-Object { $_.Path -eq 'new.txt' }).Hash)
$plan = $P::PlanCommit($v, $step, '', $picked)
Check 'scope: an explicit pick commits exactly the picked file' ($plan.Kind -eq 'run' -and @($plan.Files).Count -eq 1 -and $plan.Files[0].Path -eq 'new.txt')
BW "$scope\new.txt" "changed after the pick`n"
$plan = $P::PlanCommit([ClaudeStatus.Git]::Inspect($scope), $step, '', $picked)
Check 'scope: a picked file that changed after the pick (stale phone) -> pick again, nothing committed' ($plan.Kind -eq 'select' -and $plan.Note -match 'changed since you picked') $plan.Note
$null = BG $scope checkout -q -b other
$plan = $P::PlanCommit([ClaudeStatus.Git]::Inspect($scope), $step, '', $null)
Check 'scope: the branch changed since the step started -> no commit, says which branch' ($plan.Kind -eq 'blocked' -and $plan.Note -match 'branch changed' -and $plan.Note -match 'main') $plan.Note
$null = BG $scope checkout -q main
BW "$scope\elsewhere.txt" "x`n"; $null = BG $scope add elsewhere.txt; $null = BG $scope commit -q -m 'someone else committed'
$plan = $P::PlanCommit([ClaudeStatus.Git]::Inspect($scope), $step, '', $null)
Check 'scope: HEAD moved since the step started (another commit) -> no commit' ($plan.Kind -eq 'blocked' -and $plan.Note -match 'HEAD moved') $plan.Note
$hp = $P::HandoffPrompt((& { $r = New-Object ClaudeStatus.BatchRun; $r.Name = 'B'; $a = New-Object ClaudeStatus.BatchRunStep; $a.Title = 'first'; $a.Project = $repoA; $c = New-Object ClaudeStatus.BatchRunStep; $c.Prompt = "The prompt`nexactly."; $r.Steps.Add($a); $r.Steps.Add($c); $r }), 1, ('x' * 9000 + 'THE END'), 'C:\h.md')
Check 'handoff: a long final response is shortened to its end, says so, points to the full file, then the prompt unchanged' ($hp -match 'END of step 1' -and $hp -match '9,007 characters' -and $hp.Contains('THE END') -and $hp.Contains('C:\h.md') -and $hp.EndsWith("The prompt`nexactly.") -and $hp.Length -lt 7000)

# ---------------------------------------------------------------------------------------------------------------
Section 'Batches: a representative batch (commit by the Git runner, continued conversation, project switch with handoff, review from the phone)'
BW "$repoA\notes.txt" "notes`nedited by you before the batch`n"   # an unrelated change of yours: must stay uncommitted
$beforeA = Count $repoA; $beforeB = Count $repoB
$rep = NewBatch 'Representative' @(
    (NewStep @{ Title = 'Fix the findings'; Project = $repoA; Prompt = "Fix the audit findings.`nMOCK:edit=one.txt"; Commit = $true; CommitMessage = "Fix audit findings`n`nDetails: one.txt — ünïcode, `"quotes`" & #hash" }),
    (NewStep @{ Title = 'Tidy up'; Project = $repoA; Prompt = "Now tidy up what you just did. MOCK:edit=two.txt"; Conversation = 'continue' }),
    (NewStep @{ Title = 'Docs (other project)'; Project = $repoB; Prompt = "Update the docs using the handoff. MOCK:ok"; Handoff = $true; Review = $true }),
    (NewStep @{ Title = 'Docs change'; Project = $repoB; Prompt = 'MOCK:edit=docs/four.md'; Commit = $true; CommitMessage = 'Docs: four' })
)
$null = SaveB $rep
BClear
$board = BBoard
$def = @($board.queue.defs | Where-Object { $_.nm -eq 'Representative' })[0]
Check 'phone: the saved batch is listed with its steps (friendly names, no paths)' ($def -and $def.n -eq 4 -and $def.st[0] -match 'Fix the findings' -and -not ($board.queue.defs | ConvertTo-Json -Depth 6).Contains($bt.Replace('\', '\\')))
$runBody = @{ op = 'brun'; id = (BId); k = $def.k }
$r1 = BPost $runBody
$r1b = BPost $runBody
Check 'phone Run now: started on the laptop; resending the same request returns the same run (no second run)' ($r1.Code -eq 200 -and $r1.J.result -eq 'started' -and $r1b.Code -eq 200 -and $r1b.J.batch.k -eq $r1.J.batch.k -and @($bq.Data.BatchRuns | Where-Object { $_.Name -eq 'Representative' }).Count -eq 1) "$($r1.Code) $($r1.Body)"
$r2 = BPost @{ op = 'brun'; id = (BId); k = $def.k }
Check 'Run now again while a run is unfinished is refused (one run per batch at a time)' ($r2.Code -eq 409 -and $r2.J.msg -match "isn't finished") $r2.Body
$rid = @($bq.Data.BatchRuns | Where-Object { $_.Name -eq 'Representative' })[0].Id
$done = BDrive { Settled $rid } 120
$r = BRun $rid
Check 'the run pauses for review after step 3 (review checkpoint), not before' ($done -and $r.State -eq 'Paused' -and $r.PauseKind -eq 'review' -and $r.Current -eq 2) "$($r.State) $($r.PauseKind) $($r.Current) $($r.Reason)"
Check 'step 4 has not started during the review pause' ($r.Steps[3].TaskId -eq '' -and $r.Steps[3].State -eq 'pending')
$calls = @(BCalls)
Check 'three prompts ran, one at a time, in order' ($calls.Count -eq 3) "$($calls.Count)"
$s1 = $r.Steps[0]; $s2 = $r.Steps[1]; $s3 = $r.Steps[2]
Check 'step 1: a new conversation (--session-id), in its project folder' ((BArg $calls[0] '--session-id') -eq $s1.SessionId -and $calls[0].cwd -eq $repoA -and -not (@($calls[0].args) -contains '--resume'))
Check 'step 1 committed by the Git runner: exact message, byte for byte, nothing added (no Claude/AI trailer or watermark)' ((GLog $repoA) -ceq $rep.Steps[0].CommitMessage -and (BG $repoA log -1 --format=%B%n%b%n%N) -notmatch '(?i)co-authored|claude|generated with|anthropic') (GLog $repoA)
Check 'step 1 commit: your normal Git author (the repository''s own configuration)' ((BG $repoA log -1 --format='%an <%ae>') -eq 'Batch Tester <batch@example.invalid>')
Check 'step 1 commit contains only the file the step changed; your earlier unrelated edit stays uncommitted' ((BG $repoA show --name-only --format= HEAD) -eq 'one.txt' -and (BG $repoA status --porcelain) -match 'M notes.txt' -and (Count $repoA) -eq $beforeA + 1) (BG $repoA status --porcelain)
Check 'step 1 record: commit hash, message, repository, branch and result' ($s1.CommitState -eq 'done' -and $s1.CommitHash -eq (BG $repoA rev-parse HEAD) -and $s1.Repo -eq $repoA -and $s1.Branch -eq 'main' -and $s1.CommitNote -match 'Committed' -and @($s1.Committed) -join ',' -eq 'one.txt')
Check 'step 2 continued step 1''s real conversation (--resume <step 1''s session id>), prompt exact' ((BArg $calls[1] '--resume') -eq $s1.SessionId -and (BPrompt $calls[1]) -eq $rep.Steps[1].Prompt -and $s2.SessionId -eq $s1.SessionId)
$hist = @($r.History)
$iCommit = [Array]::FindIndex([string[]]$hist, [Predicate[string]] { param($h) $h -match 'Commit for step 1: committed' }); $iStep2 = [Array]::FindIndex([string[]]$hist, [Predicate[string]] { param($h) $h -match 'Step 2 queued' })
Check 'step 2 was queued only after step 1''s commit was recorded' ($iCommit -ge 0 -and $iStep2 -gt $iCommit -and [DateTime]::Parse($calls[1].at).ToUniversalTime() -gt $s1.EndedUtc) ($hist -join ' / ')
Check 'step 2 has no commit: its change stays in the working tree' ((BG $repoA status --porcelain) -match '\?\? two.txt' -and (Count $repoA) -eq $beforeA + 1)
$p3 = BPrompt $calls[2]
Check 'step 3 switched projects with a fresh conversation (new session id, other folder)' ((BArg $calls[2] '--session-id') -eq $s3.SessionId -and $s3.SessionId -ne $s1.SessionId -and $calls[2].cwd -eq $repoB -and -not (@($calls[2].args) -contains '--resume'))
Check 'step 3''s prompt: the visible handoff (source step and project, step 2''s final response, the handoff file), then the original prompt unchanged' ($p3 -match 'Handoff from step 2 "Tidy up"' -and $p3 -match 'project repo A' -and $p3.Contains('Edited two.txt.') -and $p3.Contains($s3.HandoffFile) -and $p3.EndsWith($rep.Steps[2].Prompt)) $p3
Check 'the original prompt is kept separately from the handoff wrapper' ($s3.Prompt -eq $rep.Steps[2].Prompt -and (TaskOf $rid 2).Prompt -ne $s3.Prompt)
$hf = [IO.File]::ReadAllText($s3.HandoffFile)
Check 'the handoff file holds the complete final response, the source step''s prompt, project and conversation' ($hf.Contains('Edited two.txt.') -and $hf.Contains($rep.Steps[1].Prompt) -and $hf.Contains($repoA) -and $hf.Contains($s1.SessionId))
Check 'each step records times, attempts, session and run log' ($s1.StartedUtc -gt [DateTime]::MinValue -and $s1.EndedUtc -ge $s1.StartedUtc -and $s1.Attempts -eq 1 -and $s1.RunLog -ne '' -and (Test-Path (Join-Path $bq.RunsDir $s1.RunLog)))
Check 'notification when the batch pauses for review' (@($script:bnotes | Where-Object { $_ -eq 'Representative|Paused|review' }).Count -eq 1) ($script:bnotes -join '; ')
# the desktop Batches tab at the review checkpoint
$bv = New-Object ClaudeStatus.BatchView($bq, [single]1)
$bv.SelectRun($rid)
Check 'desktop: the run shows its status, "Step 3 of 4", the reason, the steps with their results, and Continue' ($bv.DetailText -match 'Waiting for your review' -and $bv.DetailText -match 'Step 3 of 4' -and (@($bv.ActionTexts) -contains 'Continue') -and (@($bv.StepTexts)[0] -match 'committed') -and (@($bv.StepTexts)[1] -match 'continues')) (($bv.ActionTexts -join ',') + ' | ' + ($bv.StepTexts -join ' / '))
$bv.Dispose()
# the phone at the review checkpoint
$pr = PhoneRun 'Representative'
Check 'phone: the same run, "Step 3 of 4", review reason, per-step status with commit results, and Continue' ($pr.lb -eq 'Waiting for your review' -and $pr.pg -eq 'Step 3 of 4' -and $pr.why -match 'Review step 3' -and $pr.steps[0].cl -match 'committed' -and $pr.steps[0].ch -eq $s1.CommitHash -and @($pr.ops | Where-Object { $_.op -eq 'resume' -and $_.l -eq 'Continue' }).Count -eq 1)
Check 'phone: no session ids or folder paths in the batch data' (-not ($pr | ConvertTo-Json -Depth 8).Contains($s1.SessionId) -and -not ($pr | ConvertTo-Json -Depth 8).Contains($bt.Replace('\', '\\')))
$a = BPost @{ op = 'bact'; id = (BId); k = $pr.k; a = 'resume' } @{ 'X-Claude-Status' = 'git' }
Check 'phone actions need the page''s own header (cross-site protection)' ($a.Code -eq 403)
$a = BPost @{ op = 'bact'; id = (BId); k = $pr.k; a = 'rm -rf'; sq = $pr.sq }
Check 'phone: an unknown action is refused' ($a.Code -eq 400)
$a = BPost @{ op = 'bact'; id = (BId); k = $pr.k; a = 'retry'; sq = $pr.sq }
Check 'phone: an action that isn''t valid now (Retry during a review) is refused by the laptop' ($a.Code -eq 409 -and $a.J.msg -match "isn't possible now") $a.Body
$resume = @{ op = 'bact'; id = (BId); k = $pr.k; a = 'resume'; sq = $pr.sq }
$a = BPost $resume
$a2 = BPost $resume
$a3 = BPost @{ op = 'bact'; id = (BId); k = $pr.k; a = 'resume'; sq = $pr.sq }
Check 'phone Continue: applied once; the same request again gets the same answer; a second tap with the old version is refused' ($a.Code -eq 200 -and $a.J.result -eq 'done' -and $a2.Code -eq 200 -and $a3.Code -eq 409 -and $a3.J.msg -match 'changed since you looked') "$($a.Code) $($a2.Code) $($a3.Code) $($a3.Body)"
$done = BDrive { Settled $rid } 90
$r = BRun $rid
Check 'after Continue: step 4 ran and the batch completed' ($done -and $r.State -eq 'Completed' -and @(BCalls).Count -eq 4) "$($r.State) $($r.Reason)"
Check 'step 4 committed only its own file in the docs repository' ((GLog $repoB) -ceq 'Docs: four' -and (BG $repoB show --name-only --format= HEAD) -eq 'docs/four.md' -and (Count $repoB) -eq $beforeB + 1)
Check 'no automatic push, amend or history rewrite: repository A still has exactly one new commit on top of where it was' ((Count $repoA) -eq $beforeA + 1 -and (BG $repoA rev-parse HEAD~1) -eq $r.Steps[0].BaseHead)
Check 'strict sequence held on every tick (one step at a time, never ahead, at most one task running)' ($script:violations.Count -eq 0) ($script:violations -join '; ')
Check 'notification when the batch finished' (@($script:bnotes | Where-Object { $_ -eq 'Representative|Completed|' }).Count -eq 1) ($script:bnotes -join '; ')
# running again: a new run with fresh progress; the first one is history
$bq.SetPaused($true)
$rid2 = StartB $rep.Id
$r2 = BRun $rid2; $r = BRun $rid
Check 'Run now again: a new run with fresh progress; the finished run keeps its history' ($rid2 -ne $rid -and $r2.Steps[0].State -eq 'prompt' -and $r2.Steps[1].State -eq 'pending' -and $r.State -eq 'Completed' -and $r.Steps[0].CommitHash -ne '')
$err = Act $rid2 'cancel'
$t = TaskOf $rid2 0
Check 'Cancel before the first prompt started: the run is canceled, its queued prompt won''t run, later steps are marked canceled' ($null -eq $err -and (BRun $rid2).State -eq 'Canceled' -and $t.State -eq 'Canceled' -and (BRun $rid2).Steps[3].State -eq 'canceled') $err
$bq.SetPaused($false); $null = BDrive { -not $bq.HasRun } 5
Check 'nothing ran for the canceled run' (@(BCalls).Count -eq 4)

# ---------------------------------------------------------------------------------------------------------------
Section 'Batches: failures, unclear results, input-blocked, usage limits, pause / stop / skip / retry'
BClear
$env:MOCK_CONTINUE_DONE = '1'
$fb = NewBatch 'Failures' @(
    (NewStep @{ Title = 'Fails first'; Project = $plain; Prompt = 'MOCK:fail' }),
    (NewStep @{ Title = 'No verdict'; Project = $plain; Prompt = 'Do something and forget the result line.' }),
    (NewStep @{ Title = 'Blocked'; Project = $plain; Prompt = 'MOCK:blocked' }),
    (NewStep @{ Title = 'Last'; Project = $plain; Prompt = 'MOCK:ok' })
)
$null = SaveB $fb; $fid = StartB $fb.Id
$null = BDrive { Settled $fid }
$r = BRun $fid
Check 'a failed prompt pauses the batch with the reason; the next step doesn''t start' ($r.State -eq 'Paused' -and $r.PauseKind -eq 'prompt' -and $r.Reason -match 'Step 1 failed' -and $r.Steps[1].TaskId -eq '') $r.Reason
Check 'pause notification for a failure' (@($script:bnotes | Where-Object { $_ -eq 'Failures|Paused|prompt' }).Count -eq 1)
$sid1 = (TaskOf $fid 0).SessionId
$err = Act $fid 'retry'
$null = BDrive { Settled $fid }
$c = @(BCalls)
Check 'Retry step retries only that step, continuing its own conversation (no resend of the original prompt)' ($null -eq $err -and $c.Count -ge 2 -and (BArg $c[1] '--resume') -eq $sid1 -and (BPrompt $c[1]).StartsWith('Continue the task from your previous turn') -and (BRun $fid).Steps[0].State -eq 'done' -and (TaskOf $fid 0).Attempts.Count -eq 2) "$err"
$r = BRun $fid
Check 'a prompt that ends without Claude''s completion line pauses as an unclear result (never counted as success)' ($r.State -eq 'Paused' -and $r.PauseKind -eq 'unknown' -and $r.Current -eq 1 -and $r.Reason -match 'without confirming' -and $r.Steps[2].TaskId -eq '') $r.Reason
Check 'unclear result: the actions offered are Accept, Retry, Skip and Cancel' ((([ClaudeStatus.Batching]::Ops($r, $bq.Data)) -join ',') -eq 'accept,retry,skip,cancel')
$staleSeq = $r.Seq
$err = Act $fid 'accept'
$err2 = $bq.BatchAction($fid, 'accept', $staleSeq, $null)
Check 'Accept continues; the same action again with the old version is refused (no double action)' ($null -eq $err -and $err2 -match 'changed since you looked') $err2
$null = BDrive { Settled $fid -and (BRun $fid).Current -ge 2 }
$r = BRun $fid
Check 'input-blocked (Claude reports it is blocked) pauses with "needs input or permission" and the reason' ($r.State -eq 'Paused' -and $r.PauseKind -eq 'prompt' -and $r.Current -eq 2 -and $r.Reason -match 'needs input or permission' -and $r.Reason -match 'DEPLOY_TOKEN') $r.Reason
Check 'Skip asks first and says later steps may depend on it' (([ClaudeStatus.Batching]::Confirm('skip', $r)) -match 'Later steps may depend')
$err = Act $fid 'skip'
$null = BDrive { Settled $fid }
$r = BRun $fid
Check 'Skip moves on; the batch completes; step outcomes are recorded (done, accepted, skipped, done)' ($null -eq $err -and $r.State -eq 'Completed' -and (($r.Steps | ForEach-Object { $_.State + '/' + $_.Outcome }) -join ',') -eq 'done/completed,done/accepted,skipped/skipped,done/completed') (($r.Steps | ForEach-Object { $_.State + '/' + $_.Outcome }) -join ',')
Check 'no step ran more often than asked (5 prompts: fail, retry, no verdict, blocked, last)' (@(BCalls).Count -eq 5) "$(@(BCalls).Count)"
$env:MOCK_CONTINUE_DONE = $null

# usage limit: the step waits (the queue retries it), the batch neither pauses nor moves on
BClear
$env:MOCK_RESET_SEC = '2'
$ub = NewBatch 'Usage' @((NewStep @{ Title = 'Limited'; Project = $plain; Prompt = 'MOCK:usage' }), (NewStep @{ Title = 'After'; Project = $plain; Prompt = 'MOCK:ok' }))
$null = SaveB $ub; $uid = StartB $ub.Id
$null = BDrive { $t = TaskOf $uid 0; $t -and $t.State -eq 'WaitingUsage' } 40
$r = BRun $uid; $t = TaskOf $uid 0
Check 'usage limit: the step shows Waiting for usage; the batch keeps running and doesn''t start the next step' ($t.State -eq 'WaitingUsage' -and $r.State -eq 'Running' -and [ClaudeStatus.Batching]::StepLabel($r.Steps[0], $t) -eq 'Waiting for usage' -and $r.Steps[1].TaskId -eq '') "$($t.State) $($r.State)"
$err = Act $uid 'cancel'
Check 'Cancel while a step waits for usage: canceled, the waiting prompt won''t run again' ($null -eq $err -and (BRun $uid).State -eq 'Canceled' -and (TaskOf $uid 0).State -eq 'Canceled')
$env:MOCK_RESET_SEC = $bOldReset
Start-Sleep -Seconds 2   # the reported reset has passed (the engine waits for it before starting anything else)

# pause after the current step, then resume
BClear
$pb = NewBatch 'Pausing' @((NewStep @{ Title = 'One'; Project = $plain; Prompt = 'MOCK:edit=p1.txt' }), (NewStep @{ Title = 'Two'; Project = $plain; Prompt = 'MOCK:edit=p2.txt' }))
$null = SaveB $pb; $pid1 = StartB $pb.Id
$null = BDrive { $t = TaskOf $pid1 0; $t -and $t.State -eq 'Running' } 20
$err = Act $pid1 'pause'
Check 'Pause after current step while step 1 runs: accepted, step 1 keeps running' ($null -eq $err -and (BRun $pid1).PauseAfterStep -and (TaskOf $pid1 0).State -eq 'Running') $err
$null = BDrive { Settled $pid1 }
$r = BRun $pid1
Check 'it paused after step 1 finished, before step 2 started' ($r.State -eq 'Paused' -and $r.PauseKind -eq 'user' -and $r.Steps[0].State -eq 'done' -and $r.Steps[1].TaskId -eq '' -and $r.Reason -match 'Paused after step 1') $r.Reason
$err = Act $pid1 'resume'
$null = BDrive { Settled $pid1 }
Check 'Resume starts step 2 and the batch completes' ($null -eq $err -and (BRun $pid1).State -eq 'Completed' -and @(BCalls).Count -eq 2)

# stop the running prompt (separate from cancel), reporting whether it stopped
BClear
$sb = NewBatch 'Stopping' @((NewStep @{ Title = 'Long'; Project = $plain; Prompt = 'MOCK:child' }), (NewStep @{ Title = 'Next'; Project = $plain; Prompt = 'MOCK:ok' }))
$null = SaveB $sb; $sid = StartB $sb.Id
$null = BDrive { $t = TaskOf $sid 0; $t -and $t.State -eq 'Running' -and (Test-Path (Join-Path $bmock 'child.txt')) } 30
$r = BRun $sid
Check 'while a prompt runs, Stop current prompt is offered next to Pause and Cancel' ((([ClaudeStatus.Batching]::Ops($r, $bq.Data)) -join ',') -eq 'pause,stop,cancel')
$childPid = [int](Get-Content (Join-Path $bmock 'child.txt'))
$err = Act $sid 'stop'
$null = BDrive { Settled $sid } 20
$r = BRun $sid
Check 'Stop current prompt: Claude''s process tree is stopped and confirmed; the batch pauses (Retry / Skip / Cancel), nothing rolled back' ($null -eq $err -and -not (Get-Process -Id $childPid -ErrorAction SilentlyContinue) -and $r.State -eq 'Paused' -and $r.Reason -match 'was stopped' -and $r.Steps[1].TaskId -eq '') "$err | $($r.Reason)"
Remove-Item (Join-Path $bmock 'child.txt') -ErrorAction SilentlyContinue
$err = Act $sid 'cancel'
Check 'Cancel remaining steps after a stop: canceled, nothing more runs' ($null -eq $err -and (BRun $sid).State -eq 'Canceled' -and @(BCalls).Count -eq 1) "$err | $((BRun $sid).State) | $(@(BCalls).Count)"
Check 'cancel never claims a rollback' ((BRun $sid).Reason -match 'Nothing was rolled back')

# ---------------------------------------------------------------------------------------------------------------
Section 'Batches: commits (disabled, no changes, failed and retried, pre-existing staged / dirty files, picking from the phone)'
BClear
$cbefore = Count $repoC
$cb = NewBatch 'Commits' @(
    (NewStep @{ Title = 'No commit'; Project = $repoC; Prompt = 'MOCK:edit=c-off.txt' }),
    (NewStep @{ Title = 'Nothing to commit'; Project = $repoC; Prompt = 'MOCK:ok'; Commit = $true; CommitMessage = 'never made' }),
    (NewStep @{ Title = 'Hook says no first'; Project = $repoC; Prompt = 'MOCK:edit=c-hook.txt'; Commit = $true; CommitMessage = 'Hooked change' })
)
BW "$repoC\.git\hooks\pre-commit" "#!/bin/sh`nif [ -f block.flag ]; then echo HOOK-SAYS-NO >&2; exit 1; fi`n"
BW "$repoC\block.flag" 'x'
$null = SaveB $cb; $cid = StartB $cb.Id
$null = BDrive { Settled $cid } 90
$r = BRun $cid
Check 'commit disabled: the step''s changes are left uncommitted' ($r.Steps[0].State -eq 'done' -and -not $r.Steps[0].Commit -and (BG $repoC status --porcelain) -match '\?\? c-off.txt')
Check 'no eligible changes: "No changes to commit", recorded, and the batch continued' ($r.Steps[1].CommitState -eq 'none' -and $r.Steps[1].CommitNote -match 'No changes to commit' -and $r.Steps[1].State -eq 'done')
Check 'a commit rejected by a hook pauses the batch with Git''s output; no commit was made; hooks are not bypassed' ($r.State -eq 'Paused' -and $r.PauseKind -eq 'commit' -and $r.Steps[2].CommitState -eq 'failed' -and $r.Steps[2].CommitOut -match 'HOOK-SAYS-NO' -and $r.Steps[2].CommitOut -notmatch 'no-verify' -and (Count $repoC) -eq $cbefore) "$($r.Reason) | $($r.Steps[2].CommitOut)"
Check 'failed commit: Retry commit / Skip commit and continue / Cancel are offered' ((([ClaudeStatus.Batching]::Ops($r, $bq.Data)) | ForEach-Object { [ClaudeStatus.Batching]::OpLabel($_, $r) }) -join ',' -eq 'Retry commit,Skip commit and continue,Cancel remaining steps…')
Check 'failed commit: the prompt''s changes are kept' ((Test-Path "$repoC\c-hook.txt") -and @(BCalls).Count -eq 3)
Remove-Item "$repoC\block.flag"
$err = Act $cid 'retry'
$null = BDrive { Settled $cid } 60
$r = BRun $cid
Check 'Retry commit commits without running the prompt again' ($null -eq $err -and $r.State -eq 'Completed' -and $r.Steps[2].CommitState -eq 'done' -and @(BCalls).Count -eq 3 -and (GLog $repoC) -ceq 'Hooked change' -and (BG $repoC show --name-only --format= HEAD) -eq 'c-hook.txt' -and (Count $repoC) -eq $cbefore + 1) "$err $($r.State) $($r.Reason)"
Check 'the commit attempt history is kept' ($r.Steps[2].CommitAttempts -eq 2 -and ($r.History -match 'Commit attempt 2') -and ($r.History -match 'commit failed'))

# pre-existing staged file + the step's own change -> pick on the phone
BClear
$null = BG $repoC add c-off.txt   # yours, staged before the batch: never committed silently
BW "$repoC\notes.txt" "notes`nyours, before the batch`n"
$sel = NewBatch 'Picking' @(
    (NewStep @{ Title = 'Pick'; Project = $repoC; Prompt = 'MOCK:edit=c-pick.txt'; Commit = $true; CommitMessage = 'Picked' }),
    (NewStep @{ Title = 'Touches your file'; Project = $repoC; Prompt = 'MOCK:edit=notes.txt'; Commit = $true; CommitMessage = 'never' })
)
$null = SaveB $sel; $pkid = StartB $sel.Id
$null = BDrive { Settled $pkid } 60
$r = BRun $pkid; $head = BG $repoC rev-parse HEAD
Check 'a file staged before the step: the commit pauses for you to pick (nothing committed, nothing unstaged)' ($r.State -eq 'Paused' -and $r.PauseKind -eq 'commit' -and $r.Steps[0].CommitState -eq 'select' -and $r.Reason -match 'c-off.txt' -and (BG $repoC diff --cached --name-only) -match 'c-off.txt') $r.Reason
$pr = PhoneRun 'Picking'
Check 'phone: the files to pick, with why each is or isn''t attributed to the step; Commit selected files is offered' (@($pr.steps[0].files).Count -eq 2 -and @($pr.steps[0].files | Where-Object { $_.p -eq 'c-pick.txt' -and $_.why -eq 'changed during this step' }).Count -eq 1 -and @($pr.ops | Where-Object { $_.op -eq 'commit' }).Count -eq 1)
$f = @($pr.steps[0].files | Where-Object { $_.p -eq 'c-pick.txt' })[0]
$a = BPost @{ op = 'bact'; id = (BId); k = $pr.k; a = 'commit'; sq = $pr.sq; files = @($f.p + "`t" + $f.h) }
$null = BDrive { Settled $pkid } 30
$r = BRun $pkid
Check 'picking only the step''s file while another is staged: refused by the same safeguard as the Commit view (select it too, or unstage it)' ($a.Code -eq 200 -and $r.State -eq 'Paused' -and $r.Steps[0].CommitState -eq 'select' -and $r.Reason -match 'Already staged' -and (BG $repoC rev-parse HEAD) -eq $head) $r.Reason
$pr = PhoneRun 'Picking'
$a = BPost @{ op = 'bact'; id = (BId); k = $pr.k; a = 'commit'; sq = $pr.sq; files = @($pr.steps[0].files | ForEach-Object { $_.p + "`t" + $_.h }) }
$null = BDrive { Settled $pkid -and (BRun $pkid).Current -ge 1 } 60
$r = BRun $pkid
Check 'picking both from the phone commits exactly those two files' ($a.Code -eq 200 -and $r.Steps[0].CommitState -eq 'done' -and ((BG $repoC show --name-only --format= HEAD) -split "`n" | Sort-Object) -join ',' -eq 'c-off.txt,c-pick.txt') (BG $repoC show --name-only --format= HEAD)
Check 'a file you had edited before the step and the step changed again: pauses for picking' ($r.State -eq 'Paused' -and $r.Current -eq 1 -and $r.Steps[1].CommitState -eq 'select' -and $r.Reason -match 'notes.txt' -and $r.Reason -match 'already had changes') $r.Reason
$err = Act $pkid 'skip'
$null = BDrive { Settled $pkid } 30
Check 'Skip commit and continue: no commit, your file stays modified, the batch completes' ($null -eq $err -and (BRun $pkid).State -eq 'Completed' -and (BRun $pkid).Steps[1].CommitState -eq 'skipped' -and (BG $repoC status --porcelain) -match 'M notes.txt')
$null = BG $repoC checkout -q -- notes.txt

# ---------------------------------------------------------------------------------------------------------------
Section 'Batches: restart recovery (no rerun of completed or interrupted steps; commits reconciled with Git history)'
function New-Restarted { $e = New-Object ClaudeStatus.QueueEngine($broot); $e.ClaudeCommand = $bq.ClaudeCommand; $e.ProjectsDir = $bprojects; $e.KeepAwake = $false; $e.Grace = [TimeSpan]::Zero; $e.ActiveSessionsOverride = $bq.ActiveSessionsOverride; $e.Start(); $e }
BClear
$rb = NewBatch 'Restart' @((NewStep @{ Title = 'Quick'; Project = $plain; Prompt = 'MOCK:ok' }), (NewStep @{ Title = 'Slow'; Project = $plain; Prompt = 'MOCK:sleep=4' }), (NewStep @{ Title = 'After'; Project = $plain; Prompt = 'MOCK:ok' }))
$null = SaveB $rb; $rrid = StartB $rb.Id
$null = BDrive { $t = TaskOf $rrid 1; $t -and $t.State -eq 'Running' -and $bq.HasRun } 30
$bq.Dispose()   # Claude Status quits while step 2 runs (Claude keeps running)
$e2 = New-Restarted
$null = BDrive { Settled $rrid $e2 } 40 $e2
$r = BRun $rrid $e2
Check 'after a restart: step 2 is picked up again (not rerun), step 1 not repeated' (@(BCalls).Count -eq 2 -and $r.Steps[0].State -eq 'done' -and $r.Current -eq 1) "$(@(BCalls).Count) $($r.Current) $($r.State)"
Check 'step 2 ended without Claude''s completion line, so the batch pauses as unclear instead of moving on' ($r.State -eq 'Paused' -and $r.PauseKind -eq 'unknown' -and $r.Steps[2].TaskId -eq '')
$null = $e2.BatchAction($rrid, 'cancel', $r.Seq, $null); $e2.Dispose()
# crash: the prompt dies while Claude Status isn't running -> uncertain, paused, never replayed
BClear
$kb = NewBatch 'Crash' @((NewStep @{ Title = 'Dies'; Project = $plain; Prompt = 'MOCK:sleep=30' }), (NewStep @{ Title = 'After'; Project = $plain; Prompt = 'MOCK:ok' }))
$null = SaveB $kb; $kid = StartB $kb.Id
$null = BDrive { $t = TaskOf $kid 0; $t -and $t.State -eq 'Running' -and $bq.RunningPid -gt 0 -and @(BCalls).Count -eq 1 } 30
$mockPid = $bq.RunningPid
$bq.Dispose()
Stop-Process -Id $mockPid -Force -ErrorAction SilentlyContinue
$e3 = New-Restarted
$null = BDrive { Settled $kid $e3 } 30 $e3
$r = BRun $kid $e3
Check 'a prompt interrupted while the app was away: the batch pauses with "outcome is unknown" and nothing is rerun' ($r.State -eq 'Paused' -and $r.PauseKind -eq 'prompt' -and $r.Reason -match 'interrupted' -and @(BCalls).Count -eq 1 -and $r.Steps[1].TaskId -eq '') "$($r.Reason) | $(@(BCalls).Count)"
$null = $e3.BatchAction($kid, 'cancel', $r.Seq, $null)
Check 'canceling the batch also closes its step task that needed attention (it doesn''t linger in the Queue)' (($e3.Data.Find($r.Steps[0].TaskId)).State -eq 'Canceled')
$e3.Dispose()

# commit interrupted mid-way (the run says "running", no thread owns it): reconciled with Git history
$head = BG $repoC rev-parse HEAD; $parent = BG $repoC rev-parse 'HEAD~1'; $msg = GLog $repoC
function Interrupt-Commit([string]$runId, [string]$baseHead, [string]$message) {
    $null = $bq.Store.Mutate([Func[ClaudeStatus.QueueData, bool]] {
            param($d)
            $x = $d.FindRun($runId); $s = $x.Steps[0]
            $x.State = 'Running'; $x.PauseKind = ''; $x.Current = 0; $s.State = 'commit'; $s.CommitState = 'running'; $s.CommitHash = ''
            $s.CommitOwner = 'an-instance-that-crashed'; $s.CommitOwnerPid = 0; $s.Repo = $repoC; $s.BaseHead = $baseHead; $s.CommitMessage = $message
            $true })
}
$ib = NewBatch 'Interrupted commit' @((NewStep @{ Title = 'x'; Project = $repoC; Prompt = 'MOCK:ok'; Commit = $true; CommitMessage = 'm' }))
$null = SaveB $ib; $bq.SetPaused($true); $iid = StartB $ib.Id; $null = $bq.BatchAction($iid, 'cancel', (BRun $iid).Seq, $null); $bq.SetPaused($false)
$n0 = Count $repoC
Interrupt-Commit $iid $parent $msg
$e4 = New-Restarted; $null = BDrive { Settled $iid $e4 } 20 $e4; $r = BRun $iid $e4
Check 'restart after the commit happened: found in Git history (same parent and message), recorded, no second commit' ($r.Steps[0].CommitState -eq 'done' -and $r.Steps[0].CommitHash -eq $head -and $r.Steps[0].CommitNote -match 'found in Git history' -and (Count $repoC) -eq $n0 -and $r.State -eq 'Completed') "$($r.Steps[0].CommitState) $($r.Steps[0].CommitNote)"
$e4.Dispose()
Interrupt-Commit $iid $head 'm'
$e5 = New-Restarted; $null = BDrive { Settled $iid $e5 } 20 $e5; $r = BRun $iid $e5
Check 'restart before the commit happened (HEAD unchanged): paused, "Retry commit is safe", nothing committed' ($r.State -eq 'Paused' -and $r.PauseKind -eq 'commit' -and $r.Steps[0].CommitState -eq 'failed' -and $r.Reason -match 'Retry commit is safe' -and (Count $repoC) -eq $n0) $r.Reason
$e5.Dispose()
Interrupt-Commit $iid $parent 'a different message'
$e6 = New-Restarted; $null = BDrive { Settled $iid $e6 } 20 $e6; $r = BRun $iid $e6
Check 'restart when HEAD moved to something else: unclear, paused, never committed again by itself' ($r.State -eq 'Paused' -and $r.Steps[0].CommitState -eq 'uncertain' -and $r.Reason -match "won't commit again" -and (Count $repoC) -eq $n0) $r.Reason
$null = $e6.BatchAction($iid, 'cancel', $r.Seq, $null); $e6.Dispose()

# ---------------------------------------------------------------------------------------------------------------
Section 'Batches: coordination with the queue, the Commit view and standalone tasks'
$bq.SetPaused($false)   # the app's engine reads what the restarted ones wrote (and ticks)
BClear
$bq.SetPaused($true)
$co = NewBatch 'Coordinated' @((NewStep @{ Title = 'Batch change'; Project = $repoC; Prompt = 'MOCK:edit=batch-only.txt'; Commit = $true; CommitMessage = 'Batch only' }))
$null = SaveB $co; $coid = StartB $co.Id
$solo = [ClaudeStatus.TaskEditor]::NewTask(); $solo.Title = 'Standalone in the same repo'; $solo.Project = $repoC; $solo.Prompt = 'MOCK:edit=standalone.txt'; $solo.Runner = 'headless'; $solo.State = 'Scheduled'
$null = $bq.Save($solo, 0)
$hold = [ClaudeStatus.Batching]::HoldFor($bq.Data, $bq.Data.Find($solo.Id))
Check 'a standalone task in the same repository waits while a batch step that commits there is unfinished' ($hold -match 'Coordinated' -and $hold -match 'commits in this repository') $hold
$stepTask = TaskOf $coid 0
Check 'a step''s task can''t be edited or removed from the Queue while its batch runs' (($bq.Save([ClaudeStatus.QueueStore]::Clone($stepTask), $stepTask.Revision)) -match 'step of a batch' -and ($bq.Remove($stepTask.Id)) -match 'Cancel the batch first')
$bq.SetPaused($false)
$null = BDrive { $r = BRun $coid; $r.State -eq 'Completed' -and (($bq.Data.Find($solo.Id)).State -eq 'Completed') -and -not $bq.HasRun } 90
$c = @(BCalls)
Check 'the batch step ran first; the standalone task ran only after the batch''s commit' ($c.Count -eq 2 -and (BPrompt $c[0]) -match 'batch-only' -and (BPrompt $c[1]) -match 'standalone' -and [DateTime]::Parse($c[1].at).ToUniversalTime() -gt (BRun $coid).Steps[0].CommitStartedUtc)
Check 'the batch''s commit has only its own change, not the standalone task''s' ((BG $repoC show --name-only --format= HEAD) -eq 'batch-only.txt' -and (BG $repoC status --porcelain) -match '\?\? standalone.txt')
# the batch's conversation isn't continued by anything else while it runs; the Commit view waits while it runs
$bq.SetPaused($true)
$cv = NewBatch 'Conversation owner' @((NewStep @{ Title = 'a'; Project = $plain; Prompt = 'MOCK:ok' }), (NewStep @{ Title = 'b'; Project = $plain; Prompt = 'MOCK:sleep=20'; Conversation = 'continue' }))
$null = SaveB $cv; $cvid = StartB $cv.Id
$bq.SetPaused($false); $null = BDrive { $t = TaskOf $cvid 1; $t -and $t.State -eq 'Running' } 40
$intruder = [ClaudeStatus.TaskEditor]::NewTask(); $intruder.Title = 'Intruder'; $intruder.Project = $plain; $intruder.Prompt = 'x'; $intruder.Conversation = 'resume'; $intruder.ResumeSession = (BRun $cvid).Steps[0].SessionId
$hold = [ClaudeStatus.Batching]::HoldFor($bq.Data, $intruder)
Check 'a standalone task can''t inject a prompt into a running batch''s conversation (it waits)' ($hold -match 'belongs to the batch') $hold
Check 'the Commit view''s manual commit waits while a batch runs in that repository (other repositories stay free)' ($bq.BatchBusyReason($plain) -match 'Pause after current step' -and $bq.BatchBusyReason($repoB) -eq '')
$null = Act $cvid 'stop'; $null = BDrive { Settled $cvid } 20
$null = Act $cvid 'resume'   # not possible while it's paused for a stopped prompt: refused, nothing changes
Check 'and free again once the batch is paused' ($bq.BatchBusyReason($plain) -eq '' -and (BRun $cvid).State -eq 'Paused')
$null = Act $cvid 'cancel'; $bq.SetPaused($false)
Check 'one batch at a time: a second batch waits while another is active' ($(
        $bq.SetPaused($true)
        $w1 = NewBatch 'First' @((NewStep @{ Project = $plain; Prompt = 'MOCK:ok' })); $w2 = NewBatch 'Second' @((NewStep @{ Project = $plain; Prompt = 'MOCK:ok' }))
        $null = SaveB $w1; $null = SaveB $w2; $i1 = StartB $w1.Id; $i2 = StartB $w2.Id; $bq.Tick()
        $ok = (BRun $i1).State -eq 'Running' -and (BRun $i2).State -eq 'Scheduled' -and (BRun $i2).Reason -match 'one at a time'
        $null = $bq.BatchAction($i1, 'cancel', (BRun $i1).Seq, $null); $bq.Tick()
        $ok = $ok -and (BRun $i2).State -eq 'Running'
        $null = $bq.BatchAction($i2, 'cancel', (BRun $i2).Seq, $null); $bq.SetPaused($false)
        $ok))

# standalone tasks are unchanged: a tasks.json from before batches existed loads and runs
$oroot = New-Root; $odir = Join-Path $oroot 'state\queue'; $null = New-Item -ItemType Directory -Force $odir
$old = '{"Version":1,"Paused":false,"Tasks":[{"Id":"oldtask1","Title":"Old task","Project":' + (ConvertTo-Json $plain) + ',"Prompt":"MOCK:ok","State":"Scheduled","Schedule":"asap","Runner":"headless","PermissionMode":"acceptEdits","AllowCommands":[],"Attempts":[]}]}'
[IO.File]::WriteAllText((Join-Path $odir 'tasks.json'), $old)
$oe = New-Object ClaudeStatus.QueueEngine($oroot); $oe.ClaudeCommand = $bq.ClaudeCommand; $oe.ProjectsDir = $bprojects; $oe.KeepAwake = $false; $oe.Grace = [TimeSpan]::Zero; $oe.ActiveSessionsOverride = $bq.ActiveSessionsOverride; $oe.Start()
Check 'an existing tasks.json without batches loads unchanged (no batches, the task intact)' ($oe.Data.Batches.Count -eq 0 -and $oe.Data.BatchRuns.Count -eq 0 -and $oe.Data.Find('oldtask1').Title -eq 'Old task' -and $oe.Store.Warning -eq '')
$null = BDrive { $t = $oe.Data.Find('oldtask1'); $t.State -eq 'Completed' -and -not $oe.HasRun } 30 $oe
$saved = Get-Content (Join-Path $odir 'tasks.json') -Raw | ConvertFrom-Json
Check 'a standalone task still runs as before, and the file keeps its tasks' ($oe.Data.Find('oldtask1').State -eq 'Completed' -and @($saved.Tasks).Count -eq 1 -and $null -ne $saved.Batches)
$oe.Dispose()

# a definition edited after a run started doesn't change the run (frozen copy)
$fr = NewBatch 'Frozen' @((NewStep @{ Title = 'v1'; Project = $plain; Prompt = 'first version' }))
$null = SaveB $fr; $bq.SetPaused($true); $frid = StartB $fr.Id
$edit = $bq.Data.FindBatch($fr.Id); $edit.Steps[0].Prompt = 'second version'; $edit.Name = 'Frozen (edited)'
$e = $bq.SaveBatch($edit, $edit.Revision)
$fr2 = BRun $frid
Check 'editing a batch never changes a run that already started (its frozen copy and its queued prompt keep the first version)' ($null -eq $e -and $fr2.Steps[0].Prompt -eq 'first version' -and (TaskOf $frid 0).Prompt -eq 'first version' -and $fr2.Name -eq 'Frozen' -and $bq.Data.FindBatch($fr.Id).Steps[0].Prompt -eq 'second version')
$null = $bq.BatchAction($frid, 'cancel', $fr2.Seq, $null); $bq.SetPaused($false)
Check 'strict sequence held on every tick of every batch above' ($script:violations.Count -eq 0) ($script:violations -join '; ')

# ---------------------------------------------------------------------------------------------------------------
# The phone page in a real browser (opt-in): lists, a run paused for review, Continue, the laptop's answer.
$bChrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$bNode = Get-Command node -ErrorAction SilentlyContinue
if (-not $Browser) { Skip 'batches on the phone page in a browser' 'run with -Browser' }
elseif (-not $bChrome -or -not $bNode) { Skip 'batches on the phone page in a browser' 'needs Chrome or Edge, and Node.js 22+' }
else {
    Section 'Batches on the phone page in a browser (headless Chrome, emulated Android phone)'
    if (-not $RenderDir) { $RenderDir = Join-Path $tmp 'renders' }
    $null = New-Item -ItemType Directory -Force $RenderDir
    $wb = NewBatch 'Browser review' @((NewStep @{ Title = 'Check'; Project = $plain; Prompt = 'MOCK:ok'; Review = $true }), (NewStep @{ Title = 'Then'; Project = $plain; Prompt = 'MOCK:ok' }))
    $null = SaveB $wb; $wid = StartB $wb.Id
    $null = BDrive { Settled $wid } 40
    $bprof = Join-Path $tmp 'chrome-batch'
    $bc = Start-Process $bChrome -ArgumentList '--headless=new', '--remote-debugging-port=0', "--user-data-dir=`"$bprof`"", '--no-first-run', '--no-default-browser-check', '--disable-extensions', 'about:blank' -PassThru
    try {
        $pf = Join-Path $bprof 'DevToolsActivePort'
        for ($i = 0; $i -lt 100 -and -not (Test-Path $pf); $i++) { Start-Sleep -Milliseconds 100 }
        $dbgPort = if (Test-Path $pf) { (Get-Content $pf)[0] } else { '' }
        if (-not $dbgPort) { Check 'batch browser: headless browser started' $false }
        else {
            $outF = Join-Path $tmp 'batch-browser.txt'
            $np = Start-Process $bNode.Source -ArgumentList "`"$(Join-Path $PSScriptRoot 'batch-browser.mjs')`"", $dbgPort, $bp, "`"$RenderDir`"" -PassThru -NoNewWindow -RedirectStandardOutput $outF -RedirectStandardError (Join-Path $tmp 'batch-browser-err.txt')
            $deadline = (Get-Date).AddMinutes(3)
            # The laptop keeps running meanwhile: its scheduler ticks and its UI thread answers the phone.
            while (-not $np.HasExited -and (Get-Date) -lt $deadline) { $bq.Tick(); [System.Windows.Forms.Application]::DoEvents(); Invariants $bq; Start-Sleep -Milliseconds 100 }
            if (-not $np.HasExited) { $np.Kill(); Check 'batch browser: finished in time' $false }
            $lines = @(Get-Content $outF -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -match '^(PASS|FAIL)\t' })
            if (-not $lines.Count) { Check 'batch browser: produced results' $false ((Get-Content (Join-Path $tmp 'batch-browser-err.txt') -Raw -ErrorAction SilentlyContinue) + '') }
            foreach ($l in $lines) { $f = $l -split "`t"; Check ('phone browser: ' + $f[1]) ($f[0] -eq 'PASS') $(if ($f.Count -gt 2) { $f[2] } else { '' }) }
            Check 'the laptop ran the batch to the end after Continue on the phone' ((BRun $wid).State -eq 'Completed')
            Info "batch screenshots: $RenderDir\phone-batch*.png"
        }
    }
    finally { try { Stop-Process -Id $bc.Id -Force -ErrorAction SilentlyContinue } catch { } }
}

# ---------- cleanup ----------
try { $bf.Close(); $bf.Dispose() } catch { }
$env:CLAUDE_CONFIG_DIR = $bOldCfg; $env:MOCK_LOG_DIR = $bOldMock; $env:MOCK_RESET_SEC = $bOldReset; $env:MOCK_CONTINUE_DONE = $null
if ($batchStandalone) {
    Say ("`n{0} passed, {1} failed, {2} skipped" -f $script:pass, $script:fail, $script:skip) $(if ($script:fail) { 'Red' } else { 'Green' })
    if ($script:fail) { $script:failures | ForEach-Object { Say "  - $_" Red } }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    exit $(if ($script:fail) { 1 } else { 0 })
}

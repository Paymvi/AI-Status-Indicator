# VS Code runner tests: real VS Code windows (an isolated profile in a temp folder, with the bridge installed from its
# .vsix), the real scheduler, and tests\mock-interactive-claude.cs instead of Claude (no real Claude, no usage).
# Opens and closes VS Code windows and moves focus, so it's opt-in:  run-tests.ps1 -VSCode  (or run this file).
# Your own VS Code profile, extensions and windows are not touched (separate --user-data-dir / --extensions-dir).
$standalone = -not (Get-Command Check -ErrorAction SilentlyContinue)
if ($standalone) { . (Join-Path $PSScriptRoot 'common.ps1') }

Section 'VS Code runner (real VS Code, isolated profile, mock interactive Claude)'
# $env:CLAUDE_STATUS_TEST_CODE can name another Code.exe, e.g. a portable .zip build: an installed VS Code that has a
# downloaded update waiting for its restart refuses to start new instances (like this isolated one) until then.
$codeExe = @(if ($env:CLAUDE_STATUS_TEST_CODE) { $env:CLAUDE_STATUS_TEST_CODE } else { (New-Object ClaudeStatus.VsBridge($tmp)).FindCode() })
$codeCmd = if ($codeExe) { Join-Path (Split-Path $codeExe[0]) 'bin\code.cmd' } else { '' }
if (-not $codeExe -or -not (Test-Path -LiteralPath $codeCmd)) { Skip 'VS Code runner tests' 'VS Code is not installed'; return }

$vt = Join-Path $tmp 'vscode'
$userDir = Join-Path $vt 'user'; $extDir = Join-Path $vt 'ext'; $argvDir = Join-Path $vt 'argv'; $driverDir = Join-Path $vt 'driver'
foreach ($d in $userDir, $extDir, $argvDir, $driverDir) { $null = New-Item -ItemType Directory -Force $d }
# Settings for the isolated profile: no restore of old windows, no trust prompts.
$null = New-Item -ItemType Directory -Force (Join-Path $userDir 'User')
[IO.File]::WriteAllText((Join-Path $userDir 'User\settings.json'), '{"window.restoreWindows":"none","security.workspace.trust.enabled":false,"workbench.startupEditor":"none","extensions.autoUpdate":false,"update.mode":"none","telemetry.telemetryLevel":"off"}')

# The mock claude.exe (a real console program, so its argv is parsed by the C runtime rules, like claude.exe's).
$mockExe = Join-Path $vt 'bin\claude.exe'; $null = New-Item -ItemType Directory -Force (Split-Path $mockExe)
Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot 'mock-interactive-claude.cs') -Raw -Encoding UTF8) -OutputAssembly $mockExe -OutputType ConsoleApplication -ReferencedAssemblies System.Web.Extensions

# Install the bridge (and the test driver) from .vsix packages into the isolated profile: the real install path.
$pack = Join-Path $src 'vscode-bridge\install-bridge.ps1'
& $winPs -NoProfile -ExecutionPolicy Bypass -File $pack -PackageOnly (Join-Path $vt 'bridge.vsix') | Out-Null
& $winPs -NoProfile -ExecutionPolicy Bypass -File $pack -From (Join-Path $PSScriptRoot 'vscode-driver') -PackageOnly (Join-Path $vt 'driver.vsix') | Out-Null
foreach ($x in 'bridge.vsix', 'driver.vsix') { & { $ErrorActionPreference = 'Continue'; & $codeCmd --user-data-dir $userDir --extensions-dir $extDir --install-extension (Join-Path $vt $x) 2>&1 | Out-Null } }   # VS Code prints Node warnings on stderr
Check 'bridge .vsix installs with VS Code''s own --install-extension' ((Get-ChildItem $extDir -Directory | Where-Object Name -like 'claude-status.claude-status-bridge-*').Count -eq 1)

$root = New-Root
$cfgDir2 = Join-Path $vt 'claude-config'; $null = New-Item -ItemType Directory -Force (Join-Path $cfgDir2 'projects')
$env:CLAUDE_STATUS_BRIDGE_DIR = Join-Path $root 'state\vscode'
$env:MOCK_HOOK_DIR = Join-Path $root 'state\sessions'
$env:MOCK_ARGV_DIR = $argvDir
$env:CLAUDE_STATUS_TEST_DRIVER = $driverDir
$env:ANTHROPIC_API_KEY = 'sk-test-must-not-reach-claude'

$p1 = Join-Path $vt 'Proj One ü 日本'; $p2 = Join-Path $vt 'proj two'; $p3 = Join-Path $vt 'multi a'; $p4 = Join-Path $vt 'multi b'
foreach ($d in $p1, $p2, $p3, $p4) { $null = New-Item -ItemType Directory -Force $d }
$ws = Join-Path $vt 'both.code-workspace'
[IO.File]::WriteAllText($ws, (@{ folders = @(@{ path = $p3 }, @{ path = $p4 }) } | ConvertTo-Json))

function New-VsEngine {
    $e = New-Object ClaudeStatus.QueueEngine($root)
    $e.ClaudeCommand = @($mockExe)
    $e.ProjectsDir = Join-Path $cfgDir2 'projects'
    $e.ActiveSessionsOverride = [Func[System.Collections.Generic.List[ClaudeStatus.ActiveSession]]] { , (New-Object 'System.Collections.Generic.List[ClaudeStatus.ActiveSession]') }
    $e.Grace = [TimeSpan]::Zero
    $e.KeepAwake = $false
    $e.Bridge.CodeCommand = @($codeExe[0], '--user-data-dir', $userDir, '--extensions-dir', $extDir, '--skip-welcome', '--skip-release-notes', '--disable-workspace-trust')
    $e.Bridge.InstalledOverride = [Func[bool]] { $true }
    $e.Start()
    return $e
}
$eng = New-VsEngine

function VsTask([string]$title, [string]$project, [string]$prompt, [hashtable]$set = @{}) {
    $t = [ClaudeStatus.TaskEditor]::NewTask(); $t.Title = $title; $t.Project = $project; $t.Prompt = $prompt; $t.State = 'Scheduled'
    foreach ($k in $set.Keys) { $t.$k = $set[$k] }
    $err = $eng.Save($t, 0); if ($err) { throw "Save failed: $err" }
    return $t.Id
}
function T([string]$id) { $eng.Data.Find($id) }
function WaitFor([scriptblock]$cond, [int]$sec = 60) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $sec) { $eng.Tick(); if (& $cond) { return $true }; Start-Sleep -Milliseconds 300 }
    return $false
}
function Windows { @($eng.Bridge.Windows()) }
function WindowsFor([string]$folder) { ,@(Windows | Where-Object { $f = $_.Folders; $f.Count -eq 1 -and [ClaudeStatus.Sched]::SamePath($f[0], $folder) }) }
function Argv([string]$sid) { ,@(Get-ChildItem $argvDir -Filter "$sid.*.json" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json }) }
function DriverStates { @(Get-ChildItem $driverDir -Filter 'state-*.json' | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } | Where-Object { $_.at -gt ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - 5000) }) }
function DriverFor([string]$folder) { ,@(DriverStates | Where-Object { $_.folders.Count -eq 1 -and [ClaudeStatus.Sched]::SamePath($_.folders[0], $folder) }) }
function TypeInto([string]$name, [string]$text) { [IO.File]::WriteAllText((Join-Path $driverDir ("cmd-" + [Guid]::NewGuid().ToString('N') + '.json')), (@{ op = 'type'; name = $name; text = $text } | ConvertTo-Json)) }

try {
    # --- 1. a project that is already open: its window is reused --------------------------------------------------
    $null = $eng.Bridge.Open($p1)
    Check 'a VS Code window opens and the bridge registers its folder (full path, spaces, Unicode)' (WaitFor { (WindowsFor $p1).Count -eq 1 } 90)
    $w1 = (WindowsFor $p1)[0].Id
    $prompt = "Line one with `"quotes`" and 'single' & | % ^ < > `$env:PATH`r`n-starts with a dash`n`tTabbed ü 日本 🚀`n`nlast line \\ trailing backslash\"
    $t1 = VsTask 'Multi line task' $p1.ToUpperInvariant() $prompt   # different casing: still the same project
    Check 'scheduled task starts in VS Code and completes when Claude''s Stop hook arrives' (WaitFor { (T $t1).State -eq 'Completed' } 60) "$((T $t1).State) $((T $t1).Reason)"
    $a = Argv (T $t1).SessionId
    Check 'exactly one Claude process for the task (no duplicate launch)' ($a.Count -eq 1) "$($a.Count)"
    $argv = @($a[0].argv)
    Check 'the multiline prompt reaches Claude byte for byte as one argument (after "--")' ($argv[-1] -ceq $prompt -and $argv[-2] -eq '--') ($argv[-1])
    Check 'Claude runs in the project folder' ([ClaudeStatus.Sched]::SamePath($a[0].cwd, $p1)) $a[0].cwd
    Check 'permissions are passed as before (mode + generated --settings), never a bypass' (($argv -join ' ') -match '--permission-mode acceptEdits' -and ($argv -join ' ') -match '--settings ' -and ($argv -join ' ') -notmatch 'dangerously|bypass')
    Check 'ANTHROPIC_API_KEY is removed from the terminal (subscription sign-in only)' ($a[0].apiKey -eq '') $a[0].apiKey
    Check 'the open project window was reused (still exactly one window for it)' ((WindowsFor $p1).Count -eq 1 -and (WindowsFor $p1)[0].Id -eq $w1)
    Check 'the terminal has a recognizable task name in that window' (WaitFor { @(DriverFor $p1 | Where-Object { $_.terminals -contains ([char]0x23F1 + ' Multi line task') }).Count -eq 1 } 10)
    Check 'completed run keeps its terminal (and Claude) open for follow-ups' ($eng.Bridge.TerminalOpen($w1, (T $t1).Last.Req))

    # --- 2. a project that isn't open: a dedicated new window; the other window isn't replaced ---------------------
    $t2 = VsTask 'Second project' $p2 'hello from task two'
    Check 'a project that isn''t open gets its own new window, and the task completes there' (WaitFor { (T $t2).State -eq 'Completed' } 90) "$((T $t2).State) $((T $t2).Reason)"
    Check 'the first window still shows its own project (never replaced)' ((WindowsFor $p1).Count -eq 1 -and (WindowsFor $p2).Count -eq 1)
    $w2 = (WindowsFor $p2)[0].Id

    # --- 3. several windows open: routed to the right one ---------------------------------------------------------
    $t3 = VsTask 'Routed to one' $p1 'route me'
    Check 'with two windows open, the task runs in its own project''s window' (WaitFor { (T $t3).State -eq 'Completed' } 60) "$((T $t3).Reason)"
    Check 'its terminal is in the first project''s window (not the other one)' ((T $t3).Last.Window -eq $w1 -and ((DriverFor $p1).terminals -contains ([char]0x23F1 + ' Routed to one')) -and -not ((DriverFor $p2).terminals -contains ([char]0x23F1 + ' Routed to one')))
    Check 'no extra window was opened for it' ((Windows).Count -eq 2) "$((Windows).Count)"

    # --- 4. Claude asks something: Needs you, answered in the VS Code terminal -------------------------------------
    $t4 = VsTask 'Asks a question' $p2 'please ASK me something'
    Check 'a question shows as Needs you on the running task (with the hook detail)' (WaitFor { (T $t4).Last -and (T $t4).Last.Phase -eq 'needs_you' } 60) "$((T $t4).Last.Phase)"
    Check 'the task says where to answer' ((T $t4).Reason -match 'answer Claude in VS Code' -and (T $t4).Last.Attention -ne '') "$((T $t4).Reason) / $((T $t4).Last.Attention)"
    TypeInto ([char]0x23F1 + ' Asks a question') 'blue'
    Check 'typing the answer in the task''s terminal reaches Claude and the task completes' (WaitFor { (T $t4).State -eq 'Completed' } 30)
    Check 'the answer arrived exactly' ((Get-Content (Join-Path $argvDir ((T $t4).SessionId + '.answer.txt')) -Raw -ErrorAction SilentlyContinue) -eq 'blue')

    # --- 5. Open in VS Code reveals the existing terminal (no second Claude) ---------------------------------------
    $before = (Argv (T $t1).SessionId).Count
    $note = $eng.OpenInVsCode($t1)
    Check 'Open in VS Code on a task whose terminal is open just reveals it' ($null -eq $note -and (WaitFor { (DriverFor $p1).active -eq ([char]0x23F1 + ' Multi line task') } 15)) "$note / $((DriverFor $p1).active)"
    Check 'revealing never starts another Claude process' ((Argv (T $t1).SessionId).Count -eq $before)

    # --- 6. usage limit -> retry resumes the same conversation after closing the old terminal ----------------------
    $t5 = VsTask 'Hits the limit' $p1 'SLOW then FAILLIMIT'
    $null = WaitFor { (T $t5).State -eq 'Running' -and (T $t5).SessionId } 30
    $sid5 = (T $t5).SessionId
    # A transcript, as Claude Code writes one, so the conversation counts as resumable.
    $tdir = Join-Path (Join-Path $cfgDir2 'projects') ([ClaudeStatus.Sessions]::Mangle($p1)); $null = New-Item -ItemType Directory -Force $tdir
    [IO.File]::WriteAllText((Join-Path $tdir "$sid5.jsonl"), (@{ type = 'user'; cwd = $p1; sessionId = $sid5; message = @{ role = 'user'; content = 'SLOW then FAILLIMIT' } } | ConvertTo-Json -Compress -Depth 5) + "`n")
    Check 'a usage limit in VS Code waits for usage and plans to continue the conversation' (WaitFor { (T $t5).State -eq 'WaitingUsage' } 60) "$((T $t5).State) $((T $t5).Reason)"
    Check '... saying it continues the same conversation' ((T $t5).NeedsContinue -and (T $t5).Reason -match 'continues the same conversation') "$((T $t5).Reason)"
    $firstPid = (Argv $sid5)[0].pid
    $eng.SetRetryTime($t5, [DateTime]::UtcNow.AddSeconds(-1))
    Check 'the retry closes the finished terminal first, then continues in a new one' (WaitFor { (T $t5).State -eq 'Completed' } 90) "$((T $t5).State) $((T $t5).Reason) | attempts: $(((T $t5).Attempts | ForEach-Object { "$($_.N):$($_.Mode):$($_.Outcome):$($_.Phase):$($_.StartedUtc.ToString('HH:mm:ss.fff'))-$($_.EndedUtc.ToString('HH:mm:ss.fff'))" }) -join ' ')"
    $runs = Argv $sid5
    Check 'the continuation resumes the same session ID (no new conversation)' ($runs.Count -eq 2 -and ($runs[1].argv -contains '--resume') -and ($runs[1].argv -contains $sid5)) (($runs | ForEach-Object { $_.argv -join ' ' }) -join ' || ')
    Check 'the continuation never resends the original prompt' ($runs[1].argv[-1] -ne 'SLOW then FAILLIMIT' -and $runs[1].argv[-1] -match 'Continue the task')
    Check 'the old Claude process had exited before the new one started' (-not (Get-Process -Id $firstPid -ErrorAction SilentlyContinue) -or (Get-Process -Id $firstPid).StartTime -gt (Get-Date).AddMinutes(-1))

    # --- 7. manual resume of a finished task: no prompt, guarded against retries ----------------------------------
    $tdir2 = Join-Path (Join-Path $cfgDir2 'projects') ([ClaudeStatus.Sessions]::Mangle($p2)); $null = New-Item -ItemType Directory -Force $tdir2
    $sid2 = (T $t2).SessionId
    [IO.File]::WriteAllText((Join-Path $tdir2 "$sid2.jsonl"), (@{ type = 'user'; cwd = $p2; sessionId = $sid2; message = @{ role = 'user'; content = 'hello from task two' } } | ConvertTo-Json -Compress -Depth 5) + "`n")
    TypeInto ([char]0x23F1 + ' Second project') '/exit'                          # you close Claude in that terminal
    Check 'closing Claude in its terminal is noticed' (WaitFor { -not $eng.Bridge.TerminalOpen($w2, (T $t2).Last.Req) -or -not (Get-Process -Id (Argv $sid2)[0].pid -ErrorAction SilentlyContinue) } 20)
    $n = $eng.OpenInVsCode($t2)
    Check 'Open in VS Code on a finished task resumes its conversation in a new terminal' ($null -eq $n -and (WaitFor { (Argv $sid2).Count -eq 2 } 30)) "$n"
    $m = (Argv $sid2)[-1].argv
    Check '... with --resume <same id> and no prompt at all (nothing is resubmitted)' (($m -contains '--resume') -and ($m -contains $sid2) -and -not ($m -contains '--')) ($m -join ' ')
    $eng.Retry($t2, $true)
    Check 'while you have it open yourself, the queue doesn''t retry it (waits, no second Claude)' (-not (WaitFor { (Argv $sid2).Count -gt 2 } 6) -and (T $t2).Reason -match 'opened this task''s conversation in VS Code yourself') "$((T $t2).State) $((T $t2).Reason)"
    $eng.Cancel($t2)

    # --- 8. cancellation, restart recovery, duplicate protection ---------------------------------------------------
    $t6 = VsTask 'Slow one to cancel' $p1 'SLOW work'
    Check 'running task reaches Working' (WaitFor { (T $t6).Last -and (T $t6).Last.Phase -eq 'working' } 60)
    $cpid = (T $t6).Last.ClaudePid
    $eng.Cancel($t6)
    Check 'Cancel stops Claude in the terminal and records Canceled (not completed)' ((T $t6).State -eq 'Canceled' -and (WaitFor { -not (Get-Process -Id $cpid -ErrorAction SilentlyContinue) } 10))

    $t7 = VsTask 'Survives a restart' $p1 'SLOW but steady'
    Check 'restart test task starts' (WaitFor { (T $t7).Last -and (T $t7).Last.Phase -eq 'working' } 60)
    $eng.Dispose()
    $eng = New-VsEngine
    $eng2 = New-VsEngine   # a second app instance ticking the same queue
    for ($i = 0; $i -lt 5; $i++) { $eng.Tick(); $eng2.Tick() }
    Check 'after an app restart the running VS Code task is picked up again and completes' (WaitFor { (T $t7).State -eq 'Completed' } 60) "$((T $t7).State) $((T $t7).Reason)"
    Check 'restart and two instances never launch it twice' ((Argv (T $t7).SessionId).Count -eq 1) "$((Argv (T $t7).SessionId).Count)"
    $eng2.Dispose()

    # --- 9. dependencies wait for a confirmed outcome ---------------------------------------------------------------
    $t8 = VsTask 'Predecessor asks' $p2 'ASK first'
    $t9 = VsTask 'Runs after it' $p2 'then me' @{ Schedule = 'after'; After = $t8 }
    Check 'predecessor waits for your answer' (WaitFor { (T $t8).Last -and (T $t8).Last.Phase -eq 'needs_you' } 60)
    for ($i = 0; $i -lt 3; $i++) { $eng.Tick(); Start-Sleep -Milliseconds 300 }
    Check 'the dependent task stays waiting while the predecessor needs you' ((T $t9).State -eq 'Scheduled' -and (T $t9).Attempts.Count -eq 0) "$((T $t9).State)"
    TypeInto ([char]0x23F1 + ' Predecessor asks') 'ok'
    Check 'after the predecessor completes, the dependent task runs' (WaitFor { (T $t9).State -eq 'Completed' } 90) "$((T $t9).State) $((T $t9).Reason)"
    $t10 = VsTask 'Canceled first' $p2 'SLOW'
    $t11 = VsTask 'Blocked after' $p2 'never' @{ Schedule = 'after'; After = $t10 }
    $null = WaitFor { (T $t10).State -eq 'Running' } 30; $eng.Cancel($t10); $eng.Tick()
    Check 'a canceled predecessor blocks its dependent (never runs on its own)' ((T $t11).State -eq 'Blocked' -and (T $t11).Attempts.Count -eq 0)

    # --- 10. multi-root workspaces -----------------------------------------------------------------------------------
    $null = $eng.Bridge.Open($ws)
    Check 'a multi-root workspace window registers both folders' (WaitFor { @(Windows | Where-Object { $_.Folders.Count -eq 2 }).Count -eq 1 } 60)
    $t12 = VsTask 'In a multi-root folder' $p3 'multi root'
    Check 'a folder that is a root of exactly one multi-root window runs there (no extra window)' ((WaitFor { (T $t12).State -eq 'Completed' } 60) -and @(Windows).Count -eq 3) "$((T $t12).Reason) / $(@(Windows).Count) windows"

    # --- 11. VS Code can't become ready: a clear failure, never a late or silent start -----------------------------
    $eng.Bridge.CodeCommand = @((Join-Path $env:WINDIR 'System32\where.exe'))   # "opens" nothing
    $eng.OpenTimeout = [TimeSpan]::FromSeconds(4)
    $p5 = Join-Path $vt 'never opened'; $null = New-Item -ItemType Directory -Force $p5
    $t13 = VsTask 'No VS Code' $p5 'hello'
    Check 'if VS Code never takes the task: Needs attention with the reason' (WaitFor { (T $t13).State -eq 'NeedsAttention' } 20) "$((T $t13).Reason)"
    Check '... the request was withdrawn (it can''t start later) and nothing ran in the background' (-not (Test-Path (Join-Path $root "state\vscode\inbox\$t13-1.json")) -and -not (Test-Path (Join-Path $root "state\queue\runs\$t13\1.jsonl")) -and (T $t13).Reason -match "didn't take the task")
    $eng.Bridge.InstalledOverride = [Func[bool]] { $false }
    $eng.Retry($t13, $false)
    Check 'bridge not installed: Needs attention saying so (no silent fallback to the background)' (WaitFor { (T $t13).State -eq 'NeedsAttention' -and (T $t13).Reason -match 'Bridge extension' } 10) "$((T $t13).Reason)"
}
finally {
    try { $eng.Dispose() } catch { }
    # Close only the isolated VS Code instance (its command line names our temp profile).
    Get-CimInstance Win32_Process -Filter "Name='Code.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($userDir) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Get-Process -Name claude -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $mockExe } | Stop-Process -Force -ErrorAction SilentlyContinue
    foreach ($v in 'CLAUDE_STATUS_BRIDGE_DIR', 'MOCK_HOOK_DIR', 'MOCK_ARGV_DIR', 'CLAUDE_STATUS_TEST_DRIVER', 'ANTHROPIC_API_KEY') { Remove-Item "env:$v" -ErrorAction SilentlyContinue }
}
if ($standalone) { Say "`n$($script:pass) passed, $($script:fail) failed, $($script:skip) skipped" $(if ($script:fail) { 'Red' } else { 'Green' }); if ($script:fail) { $script:failures | ForEach-Object { Say "  FAIL $_" Red } } }

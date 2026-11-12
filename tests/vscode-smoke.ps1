# Real-Claude smoke test of the VS Code runner (opt-in; uses a little of your Claude usage with the haiku model).
#   powershell -ExecutionPolicy Bypass -File tests\vscode-smoke.ps1
# Runs real interactive claude.exe in a terminal of an isolated VS Code profile (temp folder, bridge installed from its
# .vsix), in a disposable git-ignored folder (state\smoke-*, deleted afterwards). Claude uses your normal sign-in and
# your installed Claude Status hooks, so the scheduler here reads their state files (state\sessions of your install,
# read only).
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('claude-status-smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
. (Join-Path $PSScriptRoot 'common.ps1')
Section 'VS Code runner with real Claude (smoke test)'
$cfgHome = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
$hooksDir = Join-Path $cfgHome 'status-indicator\state\sessions'
if (-not (Test-Path $hooksDir)) { Skip 'smoke test' 'Claude Status is not installed (its hooks report the progress)'; return }
$codeExe = @(if ($env:CLAUDE_STATUS_TEST_CODE) { $env:CLAUDE_STATUS_TEST_CODE } else { (New-Object ClaudeStatus.VsBridge($tmp)).FindCode() })
if (-not $codeExe -or -not $codeExe[0]) { Skip 'smoke test' 'VS Code not found'; return }
$codeCmd = Join-Path (Split-Path $codeExe[0]) 'bin\code.cmd'

$vt = Join-Path $tmp 'vscode'; $userDir = Join-Path $vt 'user'; $extDir = Join-Path $vt 'ext'; $driverDir = Join-Path $vt 'driver'
foreach ($d in $userDir, $extDir, $driverDir, (Join-Path $userDir 'User')) { $null = New-Item -ItemType Directory -Force $d }
[IO.File]::WriteAllText((Join-Path $userDir 'User\settings.json'), '{"window.restoreWindows":"none","security.workspace.trust.enabled":false,"workbench.startupEditor":"none","update.mode":"none","telemetry.telemetryLevel":"off"}')
$pack = Join-Path $src 'vscode-bridge\install-bridge.ps1'
& $winPs -NoProfile -ExecutionPolicy Bypass -File $pack -PackageOnly (Join-Path $vt 'bridge.vsix') | Out-Null
& $winPs -NoProfile -ExecutionPolicy Bypass -File $pack -From (Join-Path $PSScriptRoot 'vscode-driver') -PackageOnly (Join-Path $vt 'driver.vsix') | Out-Null
foreach ($x in 'bridge.vsix', 'driver.vsix') { & { $ErrorActionPreference = 'Continue'; & $codeCmd --user-data-dir $userDir --extensions-dir $extDir --install-extension (Join-Path $vt $x) 2>&1 | Out-Null } }

$root = New-Root
Remove-Item (Join-Path $root 'state\sessions') -Recurse -Force
$null = New-Item -ItemType Junction -Path (Join-Path $root 'state\sessions') -Target $hooksDir   # read the real hooks' state
$env:CLAUDE_STATUS_BRIDGE_DIR = Join-Path $root 'state\vscode'
$env:CLAUDE_STATUS_TEST_DRIVER = $driverDir
# The project is a fresh, git-ignored folder inside this repository: Claude Code trusts it if you trusted the repo
# (a brand-new temp folder would stop at Claude's "trust this folder?" screen, which only a person should answer).
$proj = Join-Path $repo ('state\smoke ü ' + [Guid]::NewGuid().ToString('N').Substring(0, 6)); $null = New-Item -ItemType Directory -Force $proj
[IO.File]::WriteAllText((Join-Path $proj 'README.md'), "Disposable folder for a Claude Status smoke test.`n")

$eng = New-Object ClaudeStatus.QueueEngine($root)
$eng.Grace = [TimeSpan]::Zero; $eng.KeepAwake = $false
$debugLog = Join-Path $tmp 'claude-debug.txt'   # Claude's own log, in case a run fails
$eng.ClaudeCommand = @((Join-Path $env:USERPROFILE '.local\bin\claude.exe'), '--debug-file', $debugLog)
$eng.Bridge.CodeCommand = @($codeExe[0], '--user-data-dir', $userDir, '--extensions-dir', $extDir, '--skip-welcome', '--skip-release-notes', '--disable-workspace-trust')
$eng.Bridge.InstalledOverride = [Func[bool]] { $true }
$eng.Start()
function T([string]$id) { $eng.Data.Find($id) }
function WaitFor([scriptblock]$cond, [int]$sec = 120, [scriptblock]$each = $null) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $sec) { $eng.Tick(); if (& $cond) { return $true }; if ($each) { & $each }; Start-Sleep -Milliseconds 500 }
    return $false
}
function TypeInto([string]$name, [string]$text, [bool]$enter = $true) { [IO.File]::WriteAllText((Join-Path $driverDir ("cmd-" + [Guid]::NewGuid().ToString('N') + '.json')), (@{ op = 'type'; name = $name; text = $text; enter = $enter } | ConvertTo-Json)) }
function Task([string]$title, [string]$prompt) {
    $t = [ClaudeStatus.TaskEditor]::NewTask(); $t.Title = $title; $t.Project = $proj; $t.Prompt = $prompt; $t.State = 'Scheduled'; $t.Model = 'haiku'
    $e = $eng.Save($t, 0); if ($e) { throw $e }; $t.Id
}

try {
    $prompt = "Reply with exactly the word PONG.`nDo not use any tools.`n`"Quotes`" & 100% ü 日本"
    $script:cur = Task 'Smoke: pong' $prompt
    $ok = WaitFor { (T $script:cur).State -ne 'Running' -and (T $script:cur).State -ne 'Queued' -and (T $script:cur).State -ne 'Scheduled' } 240
    $t1 = T $script:cur
    Check 'real Claude: the task starts in a VS Code terminal and is Completed from its own Stop hook' ($ok -and $t1.State -eq 'Completed') "$($t1.State): $($t1.Reason)"
    Check 'real Claude: Claude really answered (a model turn ended normally, per its debug log)' ((Get-Content -LiteralPath $debugLog -Raw) -match 'turn 1 end .*stop=end_turn')
    Info ("result captured at completion: '" + $t1.ResultText + "' (empty if Claude hadn't saved the conversation yet)")

    # Leave Claude cleanly (as you would), so it saves the conversation; then check it and resume it.
    TypeInto $t1.Last.Terminal '/exit'
    $null = WaitFor { $t1.Last.ClaudePid -gt 0 -and -not (Get-Process -Id $t1.Last.ClaudePid -ErrorAction SilentlyContinue) } 30
    $file = $null
    for ($i = 0; $i -lt 10 -and -not $file; $i++) { $file = [ClaudeStatus.Sessions]::Find($eng.ProjectsDir, $proj, $t1.SessionId); if (-not $file) { Start-Sleep -Seconds 1 } }
    if (-not $file) { Skip 'prompt exactness and resume with real Claude' 'Claude Code saved no conversation file for this interactive session' }
    else {
        $firstUser = (Get-Content -LiteralPath $file -Encoding UTF8 | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } } | Where-Object { $_.type -eq 'user' -and $_.message.content -is [string] } | Select-Object -First 1).message.content
        Check 'real Claude: the multiline prompt (quotes, &, %, Unicode) arrived exactly, per Claude''s own transcript' ($firstUser -ceq $prompt) $firstUser
        $act = $eng.ActivityOf((T $t1.Id), (T $t1.Id).Last)
        Check 'real Claude: its reply shows in the task''s activity' ($act.LastReply -match 'PONG') $act.LastReply
        $lines = @(Get-Content -LiteralPath $file).Count
        $note = $eng.OpenInVsCode($t1.Id)
        Check 'real Claude: Open in VS Code resumes the finished conversation in a terminal' ($null -eq $note -and (WaitFor { ($s = $eng.Bridge.Status((T $t1.Id).ManualReq)) -and $s.State -eq 'open' } 60)) "$note"
        Start-Sleep -Seconds 15
        $newUser = @(Get-Content -LiteralPath $file -Encoding UTF8 | Select-Object -Skip $lines | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } } | Where-Object { $_.type -eq 'user' -and $_.message.content -is [string] -and $_.message.content -notmatch '^<' })
        Check 'real Claude: resuming sent nothing to Claude (no prompt resubmitted, same session)' ($newUser.Count -eq 0 -and (T $t1.Id).SessionId -eq $t1.SessionId) "$($newUser.Count) new user message(s)"
    }

    $script:cur = Task 'Smoke: asks a question' 'Use the AskUserQuestion tool once to ask me which color I prefer, with the options red and blue. Then reply with only the color I chose.'
    Check 'real Claude: a question shows as Needs you' (WaitFor { (T $script:cur).Last -and (T $script:cur).Last.Phase -eq 'needs_you' } 240) "$((T $script:cur).State) $((T $script:cur).Reason)"
    Info ("request shown in the app: " + (T $script:cur).Last.Attention)
    TypeInto (T $script:cur).Last.Terminal ''   # Enter picks the highlighted (first) option
    Check 'real Claude: answering in the VS Code terminal lets it finish (Completed)' (WaitFor { (T $script:cur).State -eq 'Completed' } 180) "$((T $script:cur).State): $((T $script:cur).Reason)"
}
finally {
    try { $eng.Dispose() } catch { }
    Get-CimInstance Win32_Process -Filter "Name='Code.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($userDir) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    cmd /c rmdir (Join-Path $root 'state\sessions') 2>$null   # remove the junction only (never the real hook files)
    Remove-Item -LiteralPath $proj -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($v in 'CLAUDE_STATUS_BRIDGE_DIR', 'CLAUDE_STATUS_TEST_DRIVER') { Remove-Item "env:$v" -ErrorAction SilentlyContinue }
}
Info "Claude debug log: $debugLog"
Say "`n$($script:pass) passed, $($script:fail) failed, $($script:skip) skipped" $(if ($script:fail) { 'Red' } else { 'Green' })

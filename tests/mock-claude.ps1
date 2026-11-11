# Stand-in for claude.exe in the scheduler tests. Speaks the same stream-json as
#   claude -p --output-format stream-json --verbose
# (message shapes copied from Claude Code 2.1.319) and keeps transcripts where Claude Code does
# (%CLAUDE_CONFIG_DIR%\projects\<cwd with non-alphanumerics as '-'>\<session>.jsonl).
# What it does is chosen by a word in the prompt it reads on stdin:
#   (none)            success
#   MOCK:usage        usage limit right away (rate_limit_event "rejected" with resetsAt = now + MOCK_RESET_SEC)
#   MOCK:usage-midway does some work, then hits the usage limit
#   MOCK:usage-noreset usage limit without any reset time
#   MOCK:deny         finishes, with a permission denial (git push)
#   MOCK:auth         not signed in          MOCK:network  connection error
#   MOCK:fail         error_during_execution MOCK:crash    exits with code 3 without a result
#   MOCK:sleep=N      works for N seconds, then success
#   MOCK:child        starts a child process and keeps running (for cancelling a process tree)
#   MOCK:apikey       reports apiKeySource ANTHROPIC_API_KEY, then works for 20 s
# Every call is recorded in %MOCK_LOG_DIR%\calls.jsonl: arguments, working folder, the exact prompt bytes
# (base64), whether ANTHROPIC_API_KEY was in its environment, and its process id.
$ErrorActionPreference = 'Stop'
$argv = @($args)
if ($argv.Count -ge 1 -and $argv[0] -eq 'agents') {
    if ($env:MOCK_AGENTS) { [Console]::Out.Write($env:MOCK_AGENTS) } else { [Console]::Out.Write('[]') }
    exit 0
}
$stdin = [Console]::OpenStandardInput(); $ms = New-Object IO.MemoryStream; $stdin.CopyTo($ms)
$bytes = $ms.ToArray()
$prompt = [Text.Encoding]::UTF8.GetString($bytes)
$out = New-Object IO.StreamWriter([Console]::OpenStandardOutput(), (New-Object Text.UTF8Encoding $false)); $out.AutoFlush = $true
function Emit($o) { $out.WriteLine(($o | ConvertTo-Json -Compress -Depth 8)) }

$sid = ''; $resume = $false
for ($i = 0; $i -lt $argv.Count; $i++) {
    if ($argv[$i] -eq '--session-id') { $sid = $argv[$i + 1] }
    if ($argv[$i] -eq '--resume') { $sid = $argv[$i + 1]; $resume = $true }
}
$cwd = (Get-Location).ProviderPath
if ($env:MOCK_LOG_DIR) {
    $rec = [ordered]@{ args = $argv; cwd = $cwd; prompt = [Convert]::ToBase64String($bytes); apikey = [bool]$env:ANTHROPIC_API_KEY; pid = $PID; at = (Get-Date).ToUniversalTime().ToString('o') }
    [IO.File]::AppendAllText((Join-Path $env:MOCK_LOG_DIR 'calls.jsonl'), ($rec | ConvertTo-Json -Compress -Depth 5) + "`n")
}
$projects = Join-Path $env:CLAUDE_CONFIG_DIR 'projects'
$mangled = ($cwd.TrimEnd('\').ToCharArray() | ForEach-Object { if ([int]$_ -lt 128 -and [char]::IsLetterOrDigit($_)) { $_ } else { '-' } }) -join ''
$tdir = Join-Path $projects $mangled; $null = New-Item -ItemType Directory -Force $tdir
$transcript = Join-Path $tdir "$sid.jsonl"
if ($resume -and -not (Test-Path -LiteralPath $transcript)) {
    Emit ([ordered]@{ type = 'result'; subtype = 'error_during_execution'; is_error = $true; session_id = $sid; num_turns = 0; duration_ms = 5; errors = @("No conversation found with session ID: $sid"); permission_denials = @() })
    exit 1
}
if (-not $resume -and (Test-Path -LiteralPath $transcript)) { [Console]::Error.WriteLine("Error: Session ID $sid is already in use."); exit 1 }
$ts = (Get-Date).ToUniversalTime().ToString('o')
$user = [ordered]@{ type = 'user'; cwd = $cwd; entrypoint = 'sdk-cli'; sessionId = $sid; timestamp = $ts; message = @{ role = 'user'; content = $prompt } }
[IO.File]::AppendAllText($transcript, ($user | ConvertTo-Json -Compress -Depth 5) + "`n")

$keySource = if ($prompt -match 'MOCK:apikey') { 'ANTHROPIC_API_KEY' } else { 'none' }
Emit ([ordered]@{ type = 'system'; subtype = 'init'; cwd = $cwd; session_id = $sid; apiKeySource = $keySource; permissionMode = 'acceptEdits'; model = 'mock-model'; claude_code_version = '2.1.319-mock'; tools = @('Bash', 'Edit') })
function Say([string]$text) { Emit ([ordered]@{ type = 'assistant'; session_id = $sid; message = @{ role = 'assistant'; content = @(@{ type = 'text'; text = $text }) } }) }
function Tool([string]$name, $in) { Emit ([ordered]@{ type = 'assistant'; session_id = $sid; message = @{ role = 'assistant'; content = @(@{ type = 'tool_use'; id = 'toolu_1'; name = $name; input = $in }) } }) }
function Result([string]$sub, [bool]$err, [string]$text, $denials = @(), $errors = $null, $status = $null) {
    $r = [ordered]@{ type = 'result'; subtype = $sub; is_error = $err; session_id = $sid; num_turns = 2; duration_ms = 1234; result = $text; permission_denials = @($denials); api_error_status = $status; stop_reason = 'end_turn' }
    if ($errors) { $r.errors = @($errors) }
    Emit $r
    $a = [ordered]@{ type = 'assistant'; sessionId = $sid; cwd = $cwd; timestamp = (Get-Date).ToUniversalTime().ToString('o'); message = @{ role = 'assistant'; content = @(@{ type = 'text'; text = $text }) } }
    [IO.File]::AppendAllText($transcript, ($a | ConvertTo-Json -Compress -Depth 6) + "`n")
}
function Limit([bool]$withReset) {
    $resetSec = if ($env:MOCK_RESET_SEC) { [int]$env:MOCK_RESET_SEC } else { 3600 }
    $at = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() + $resetSec
    $info = [ordered]@{ status = 'rejected'; rateLimitType = 'five_hour'; isUsingOverage = $false }
    if ($withReset) { $info.resetsAt = $at }
    Emit ([ordered]@{ type = 'rate_limit_event'; session_id = $sid; rate_limit_info = $info })
    $text = if ($withReset) { "You've hit your session limit · resets 3:45pm" } else { "You've hit your usage limit" }
    Result 'success' $true $text @() $null 429
    exit 1
}

switch -regex ($prompt) {
    'MOCK:usage-midway' { Say 'Starting: step 1 done.'; Tool 'Edit' @{ file_path = 'notes.txt' }; Limit $true }
    'MOCK:usage-noreset' { Limit $false }
    'MOCK:usage\b' { Limit $true }
    'MOCK:deny' {
        Tool 'Bash' @{ command = 'git push origin main' }
        Emit ([ordered]@{ type = 'system'; subtype = 'permission_denied'; tool_name = 'Bash'; session_id = $sid; message = 'denied' })
        Result 'success' $false 'Done editing. I could not push: git push was denied.' @(@{ tool_name = 'Bash'; tool_use_id = 'toolu_1'; tool_input = @{ command = 'git push origin main' } })
        exit 0
    }
    'MOCK:auth' { Result 'success' $true 'Not logged in · Please run /login' @() $null 401; exit 1 }
    'MOCK:network' {
        Emit ([ordered]@{ type = 'system'; subtype = 'api_retry'; attempt = 1; max_retries = 10; error = 'unknown'; session_id = $sid })
        Result 'success' $true 'API Error: Connection error. (fetch failed)'; exit 1
    }
    'MOCK:fail' { Result 'error_during_execution' $true '' @() @('boom: the tool crashed'); exit 1 }
    'MOCK:crash' { exit 3 }
    'MOCK:sleep=(\d+)' { Say 'working on it'; Start-Sleep -Seconds ([int]$Matches[1]); Result 'success' $false 'Slept and finished.'; exit 0 }
    'MOCK:child' {
        Say 'working with a helper process'
        $c = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 120' -PassThru -WindowStyle Hidden
        if ($env:MOCK_LOG_DIR) { [IO.File]::WriteAllText((Join-Path $env:MOCK_LOG_DIR 'child.txt'), "$($c.Id)") }
        Start-Sleep -Seconds 120; exit 0
    }
    'MOCK:apikey' { Start-Sleep -Seconds 20; Result 'success' $false 'billed'; exit 0 }
    default { Say "Looking at the project."; Tool 'Bash' @{ command = 'npm test' }; Result 'success' $false "All done.`nChanged 2 files; tests pass."; exit 0 }
}

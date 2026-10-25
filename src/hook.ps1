# Claude Status Indicator - hook writer.
# Invoked by Claude Code lifecycle hooks (see ~/.claude/settings.json).
# Reads the hook JSON from stdin and writes a tiny per-session state file.
# MUST print nothing to stdout (UserPromptSubmit stdout is injected into Claude's context)
# and MUST always exit 0 so it can never interfere with Claude Code.
param([string]$Action = '')

try {
    # State lives next to this script (normally %USERPROFILE%\.claude\status-indicator).
    $root = $PSScriptRoot
    $dir  = Join-Path $root 'state\sessions'
    if (-not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Force -Path $dir }

    $raw = ''
    try { [Console]::InputEncoding = New-Object System.Text.UTF8Encoding $false; $raw = [Console]::In.ReadToEnd() } catch {}
    $in = $null
    if ($raw) { $i = $raw.IndexOf('{'); if ($i -gt 0) { $raw = $raw.Substring($i) } }  # tolerate a BOM/garbage prefix
    if ($raw) { try { $in = $raw | ConvertFrom-Json } catch {} }
    $raw = $null  # the raw event (which can include the prompt) is never kept or written

    $sid = 'default'
    if ($in -and $in.session_id) { $sid = [string]$in.session_id }
    $sid = $sid -replace '[^A-Za-z0-9_-]', '_'
    $file = Join-Path $dir ($sid + '.json')

    $event = ''
    if ($in -and $in.hook_event_name) { $event = ([string]$in.hook_event_name) -replace '[^A-Za-z0-9_]', '' }

    # Optional debug log (enabled only while a file named debug.on exists). Non-sensitive fields only.
    $debug = Test-Path -LiteralPath (Join-Path $root 'debug.on')

    # Only short identifier-like values (notification type, tool name, error type) are ever stored as detail.
    function Get-SafeDetail($v, [int]$max) {
        if ($null -eq $v) { return '' }
        $s = ([string]$v) -replace '[^A-Za-z0-9_.:-]', ''
        if ($s.Length -gt $max) { $s = $s.Substring(0, $max) }
        return $s
    }

    # The previous state of this session (if any); used to keep timers and identity stable.
    $cur = $null
    try { $cur = [IO.File]::ReadAllText($file) | ConvertFrom-Json } catch {}

    $state = $null; $detail = ''; $kept = $false
    switch ($Action) {
        'working'   { $state = 'WORKING' }
        'done'      { $state = 'DONE' }
        'error'     { $state = 'ERROR'; if ($in) { $detail = Get-SafeDetail $in.error 40 } }
        'needs_you' {
            $state = 'NEEDS_YOU'
            if ($in -and $in.notification_type) { $detail = Get-SafeDetail $in.notification_type 64 }
            elseif ($in -and $in.tool_name)     { $detail = Get-SafeDetail $in.tool_name 64 }
        }
        'resume' {
            # After a permission prompt / question is answered, go back to WORKING -
            # but only if we are currently in NEEDS_YOU (never overwrite DONE/ERROR).
            if ($cur -and $cur.state -eq 'NEEDS_YOU') { $state = 'WORKING' }
        }
        'end' {
            # A finished (DONE) worker stays on the Worker Board until you dismiss it there.
            # Anything else (WORKING / NEEDS_YOU / ERROR) disappears with the session, as before.
            if ($cur -and $cur.state -eq 'DONE') { $kept = $true }
            elseif (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
        }
    }

    if ($state) {
        $now = (Get-Date).ToUniversalTime().ToString('o')
        $project = ''
        if ($in -and $in.cwd) { try { $project = Split-Path -Leaf ([string]$in.cwd) } catch {} }

        # state_since: when the CURRENT state began. Repeated events in the same state keep it.
        $since = $now
        if ($cur -and $cur.state -eq $state) {
            if ($cur.state_since) { $since = [string]$cur.state_since } elseif ($cur.updated) { $since = [string]$cur.updated }
        }
        # first_seen: when this session first appeared (keeps "#1/#2" numbering stable).
        $first = $now
        if ($cur -and $cur.first_seen) { $first = [string]$cur.first_seen }
        elseif ($cur) { try { $first = [IO.File]::GetCreationTimeUtc($file).ToString('o') } catch {} }

        # Identity of the Claude Code process that owns this session (process ID + its start time),
        # so the indicator can bring the right window forward. Claude Code sets CLAUDE_PID for hooks.
        $cpid = 0; $cstart = ''
        if ($env:CLAUDE_PID -match '^\d{1,10}$') { $cpid = [int]$env:CLAUDE_PID }
        elseif ($cur -and $cur.claude_pid) { $cpid = [int]$cur.claude_pid }
        else {
            # Older Claude Code without CLAUDE_PID: use the nearest non-shell ancestor of this hook.
            try {
                $p = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction Stop
                for ($n = 0; $n -lt 3 -and $p; $n++) {
                    $p = Get-CimInstance Win32_Process -Filter "ProcessId=$($p.ParentProcessId)" -ErrorAction Stop
                    if ($p -and $p.Name -notmatch '^(cmd|powershell|pwsh|bash|sh|conhost)\.exe$') { $cpid = [int]$p.ProcessId; break }
                }
            } catch {}
        }
        if ($cpid -gt 0) {
            if ($cur -and $cur.claude_pid -and [int]$cur.claude_pid -eq $cpid -and $cur.claude_started) { $cstart = [string]$cur.claude_started }
            else { try { $cstart = (Get-Process -Id $cpid -ErrorAction Stop).StartTime.ToUniversalTime().ToString('o') } catch { $cpid = 0 } }
        }

        $obj = [ordered]@{
            v              = 2
            session_id     = $sid
            state          = $state
            event          = $event
            detail         = $detail
            project        = $project
            updated        = $now
            state_since    = $since
            first_seen     = $first
            claude_pid     = $cpid
            claude_started = $cstart
        }
        $json = $obj | ConvertTo-Json -Compress
        # Atomic replace: write temp file then rename over the target.
        $tmp = Join-Path $dir ($sid + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
        [IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding $false))
        try {
            for ($i = 0; $i -lt 10; $i++) {
                try {
                    if (Test-Path -LiteralPath $file) { [IO.File]::Replace($tmp, $file, [NullString]::Value) } else { [IO.File]::Move($tmp, $file) }
                    break
                } catch { Start-Sleep -Milliseconds 15 }
            }
        } finally {
            if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        }
    }

    if ($debug) {
        $line = '{0} action={1} event={2} state={3} kept={4} session={5}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'), $Action, $event, $state, $kept, $sid
        Add-Content -LiteralPath (Join-Path $root 'debug.log') -Value $line
    }
} catch {
    # Swallow everything; the indicator is best-effort.
}
exit 0

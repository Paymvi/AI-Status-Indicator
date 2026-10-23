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

    $sid = 'default'
    if ($in -and $in.session_id) { $sid = [string]$in.session_id }
    $sid = $sid -replace '[^A-Za-z0-9_-]', '_'
    $file = Join-Path $dir ($sid + '.json')

    $event = ''
    if ($in -and $in.hook_event_name) { $event = [string]$in.hook_event_name }

    # Optional debug log (enabled only while a file named debug.on exists). Non-sensitive fields only.
    $debug = Test-Path -LiteralPath (Join-Path $root 'debug.on')

    $state = $null; $detail = ''
    switch ($Action) {
        'working'   { $state = 'WORKING' }
        'done'      { $state = 'DONE' }
        'error'     { $state = 'ERROR'; if ($in -and $in.error) { $detail = [string]$in.error } }
        'needs_you' {
            $state = 'NEEDS_YOU'
            if ($in -and $in.notification_type) { $detail = [string]$in.notification_type }
            elseif ($in -and $in.tool_name)     { $detail = [string]$in.tool_name }
        }
        'resume' {
            # After a permission prompt / question is answered, go back to WORKING -
            # but only if we are currently in NEEDS_YOU (never overwrite DONE/ERROR).
            $cur = $null
            if (Test-Path -LiteralPath $file) { try { $cur = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json } catch {} }
            if ($cur -and $cur.state -eq 'NEEDS_YOU') { $state = 'WORKING' }
        }
        'end' {
            if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
        }
    }

    if ($state) {
        $project = ''
        if ($in -and $in.cwd) { try { $project = Split-Path -Leaf ([string]$in.cwd) } catch {} }
        $obj = [ordered]@{
            v          = 1
            session_id = $sid
            state      = $state
            event      = $event
            detail     = $detail
            project    = $project
            updated    = (Get-Date).ToUniversalTime().ToString('o')
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
        $line = '{0} action={1} event={2} state={3} session={4}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'), $Action, $event, $state, $sid
        Add-Content -LiteralPath (Join-Path $root 'debug.log') -Value $line
    }
} catch {
    # Swallow everything; the indicator is best-effort.
}
exit 0

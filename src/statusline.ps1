# Claude Status - status line feed for the Claude Usage section.
# Claude Code runs this as its status line command ("statusLine" in ~/.claude/settings.json, set by
# install.ps1) and passes session data as JSON on stdin. Only the documented "rate_limits" numbers are
# kept: for each window (five_hour, seven_day, and spend_limit behind a Claude apps gateway) the percent
# used and the reset time. They go to state\usage-statusline.json, tagged with a one-way hash of the
# account Claude Code is signed in with, so the indicator never shows one account's usage as another's.
# Nothing else from stdin (paths, session, model, costs, ...) is stored, and nothing is sent anywhere.
#
# Output (what Claude Code shows as the status line): your own previous status line command's output, if
# you had one (install.ps1 saved it as statusline-chain.json); otherwise a short usage summary.
# Never fails loudly: any error just means an empty status line and an unchanged usage file.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
try { $utf8 = New-Object System.Text.UTF8Encoding $false; [Console]::InputEncoding = $utf8; [Console]::OutputEncoding = $utf8 } catch {}
$raw = ''
try { $raw = [Console]::In.ReadToEnd() } catch {}

function Get-Num($o, [string]$name) {
    if ($null -eq $o -or -not $o.PSObject.Properties[$name]) { return $null }
    $v = $o.$name
    if ($v -is [int] -or $v -is [long] -or $v -is [double] -or $v -is [decimal]) { return [double]$v }
    return $null
}

# Same rule as UsageMonitor.ClaudeJsonPath / Usage.AccountKey in usage.cs.
function Get-AccountKey {
    $claudeDir = Split-Path -Parent $root
    $default = Join-Path $env:USERPROFILE '.claude'
    $file = if ($claudeDir.TrimEnd('\') -ieq $default.TrimEnd('\')) { Join-Path $env:USERPROFILE '.claude.json' } else { Join-Path $claudeDir '.claude.json' }
    if (-not (Test-Path -LiteralPath $file)) { return '' }
    Add-Type -AssemblyName System.Web.Extensions
    $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $ser.MaxJsonLength = 64MB
    $d = $ser.DeserializeObject([IO.File]::ReadAllText($file))
    if ($d -isnot [System.Collections.IDictionary] -or -not $d.ContainsKey('oauthAccount')) { return '' }
    $acct = $d['oauthAccount']
    if ($acct -isnot [System.Collections.IDictionary] -or -not $acct['accountUuid']) { return '' }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $h = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('claude-status|' + ([string]$acct['accountUuid']).Trim().ToLowerInvariant())) } finally { $sha.Dispose() }
    return (($h[0..7] | ForEach-Object { $_.ToString('x2') }) -join '')
}

# Quotes one argument for a Windows command line (CommandLineToArgvW rules).
function ConvertTo-Arg([string]$s) {
    if ($s -notmatch '[\s"]' -and $s) { return $s }
    $sb = New-Object System.Text.StringBuilder '"'
    $bs = 0
    foreach ($ch in $s.ToCharArray()) {
        if ($ch -eq '\') { $bs++; continue }
        if ($ch -eq '"') { [void]$sb.Append('\', 2 * $bs + 1); $bs = 0; [void]$sb.Append('"'); continue }
        if ($bs) { [void]$sb.Append('\', $bs); $bs = 0 }
        [void]$sb.Append($ch)
    }
    if ($bs) { [void]$sb.Append('\', 2 * $bs) }
    return $sb.Append('"').ToString()
}

# Runs your previous status line command the way Claude Code would on Windows (Git Bash if installed,
# otherwise PowerShell), with the same stdin, and returns its output.
function Invoke-Chain([string]$cmd, [string]$stdin) {
    $bash = $env:CLAUDE_CODE_GIT_BASH_PATH
    if (-not $bash -or -not (Test-Path -LiteralPath $bash)) {
        $bash = $null
        $git = Get-Command git.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($git) { foreach ($c in @('..\bin\bash.exe', '..\..\bin\bash.exe')) { $p = Join-Path (Split-Path -Parent $git.Source) $c; if (Test-Path -LiteralPath $p) { $bash = (Resolve-Path -LiteralPath $p).Path; break } } }
        if (-not $bash -and (Test-Path -LiteralPath "$env:ProgramFiles\Git\bin\bash.exe")) { $bash = "$env:ProgramFiles\Git\bin\bash.exe" }
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    if ($bash) { $psi.FileName = $bash; $psi.Arguments = '-c ' + (ConvertTo-Arg $cmd) }
    else { $psi.FileName = 'powershell.exe'; $psi.Arguments = '-NoProfile -NonInteractive -Command ' + (ConvertTo-Arg $cmd) }
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
    $p = [Diagnostics.Process]::Start($psi)
    try {
        $read = $p.StandardOutput.ReadToEndAsync()
        $w = New-Object IO.StreamWriter($p.StandardInput.BaseStream, (New-Object System.Text.UTF8Encoding $false))
        $w.Write($stdin); $w.Close()
        if (-not $read.Wait(10000)) { try { $p.Kill() } catch {}; return '' }
        return $read.Result.TrimEnd("`r", "`n")
    } finally { $p.Dispose() }
}

$summary = ''
try {
    $in = $null
    if ($raw) { $i = $raw.IndexOf('{'); if ($i -ge 0) { $in = $raw.Substring($i) | ConvertFrom-Json } }
    $rl = if ($in -and $in.PSObject.Properties['rate_limits']) { $in.rate_limits } else { $null }
    $in = $null   # nothing else from the session data is used

    $now = [DateTime]::UtcNow
    $seen = [ordered]@{}
    if ($rl) {
        foreach ($name in 'five_hour', 'seven_day', 'spend_limit') {
            $w = if ($rl.PSObject.Properties[$name]) { $rl.$name } else { $null }
            $pct = Get-Num $w 'used_percentage'
            if ($null -eq $pct) { continue }
            $o = [ordered]@{ pct = [Math]::Round($pct, 2) }
            $resets = Get-Num $w 'resets_at'
            if ($null -ne $resets) { $o.resets = [long]$resets }
            if ($name -eq 'spend_limit') {
                $u = Get-Num $w 'used_usd'; $l = Get-Num $w 'limit_usd'
                if ($null -ne $u -and $null -ne $l) { $o.used_usd = [Math]::Round($u, 2); $o.limit_usd = [Math]::Round($l, 2) }
                $per = if ($w.PSObject.Properties['period']) { ([string]$w.period) -replace '[^A-Za-z_-]', '' } else { '' }
                if ($per) { $o.period = $per.Substring(0, [Math]::Min(16, $per.Length)) }
            }
            $seen[$name] = $o
        }
    }

    $part = @()
    if ($seen.Contains('five_hour')) { $part += '5h ' + [Math]::Round($seen.five_hour.pct) + '%' }
    if ($seen.Contains('seven_day')) { $part += 'wk ' + [Math]::Round($seen.seven_day.pct) + '%' }
    if ($seen.Contains('spend_limit')) { $part += 'spend ' + [Math]::Round($seen.spend_limit.pct) + '%' }
    $summary = $part -join (' ' + [char]0x00B7 + ' ')   # this file stays ASCII (Windows PowerShell reads it as ANSI)

    if ($seen.Count -gt 0) {
        $account = Get-AccountKey
        $dir = Join-Path $root 'state'
        $file = Join-Path $dir 'usage-statusline.json'
        $prev = $null
        try { if (Test-Path -LiteralPath $file) { $prev = [IO.File]::ReadAllText($file) | ConvertFrom-Json } } catch {}
        # Windows Claude Code didn't mention this time (it drops one once its reset passes) keep their last
        # value, but only for the same account.
        $windows = [ordered]@{}
        $changed = $true
        if ($prev -and [string]$prev.account -eq $account -and $prev.windows) {
            $changed = $false
            foreach ($p in $prev.windows.PSObject.Properties) {
                if ($seen.Contains($p.Name)) { continue }
                $r = Get-Num $p.Value 'resets'
                if ($null -ne $r -and $r -lt ([DateTimeOffset]$now).ToUnixTimeSeconds() - 8 * 86400) { continue }
                $windows[$p.Name] = $p.Value
            }
            foreach ($k in $seen.Keys) {
                $old = if ($prev.windows.PSObject.Properties[$k]) { $prev.windows.$k } else { $null }
                if (-not $old -or (Get-Num $old 'pct') -ne $seen[$k].pct -or (Get-Num $old 'resets') -ne $seen[$k].resets) { $changed = $true }
                elseif ($old.at) { try { if (($now - [DateTime]::Parse([string]$old.at).ToUniversalTime()).TotalSeconds -gt 60) { $changed = $true } } catch { $changed = $true } }
            }
        }
        if ($changed) {
            $stamp = $now.ToString('o')
            foreach ($k in $seen.Keys) { $seen[$k].at = $stamp; $windows[$k] = $seen[$k] }
            $json = [ordered]@{ v = 1; account = $account; windows = $windows } | ConvertTo-Json -Compress -Depth 5
            if (-not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Force -Path $dir }
            $tmp = Join-Path $dir ('usage-statusline.' + [Guid]::NewGuid().ToString('N') + '.tmp')
            [IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding $false))
            try {
                for ($i = 0; $i -lt 10; $i++) {
                    try { if (Test-Path -LiteralPath $file) { [IO.File]::Replace($tmp, $file, [NullString]::Value) } else { [IO.File]::Move($tmp, $file) }; break }
                    catch { Start-Sleep -Milliseconds 15 }
                }
            } finally { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } }
        }
    }
} catch {}

$out = $summary
try {
    $chainFile = Join-Path $root 'statusline-chain.json'
    if (Test-Path -LiteralPath $chainFile) {
        $chain = [IO.File]::ReadAllText($chainFile) | ConvertFrom-Json
        if ($chain.command) { $out = Invoke-Chain ([string]$chain.command) $raw }
    }
} catch { $out = $summary }
if ($out) { [Console]::Out.Write($out) }
exit 0

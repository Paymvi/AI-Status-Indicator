<#
.SYNOPSIS
    Installs or updates Claude Status for the current Windows user.

.DESCRIPTION
    Copies the application from .\src into %USERPROFILE%\.claude\status-indicator\ and merges
    Claude Status's lifecycle hooks into %USERPROFILE%\.claude\settings.json (backup made first).
    It also sets Claude Code's status line to statusline.ps1, which records the documented usage-limit
    data for the Claude Usage section; a status line of your own keeps running (its output is still
    what you see) and is put back by uninstall.ps1.
    Unrelated settings and hooks are preserved. Safe to run repeatedly: hooks are never duplicated
    and an existing config.json is kept.

.PARAMETER ClaudeDir
    Claude Code's user config directory. Defaults to $env:CLAUDE_CONFIG_DIR if set, else %USERPROFILE%\.claude.

.PARAMETER Autostart
    Also start Claude Status automatically when you sign in to Windows.

.PARAMETER NoStart
    Don't launch the indicator after installing.

.PARAMETER NoUsageStatusLine
    Don't set up the status line feed (and remove it if an earlier install added it). The Claude Usage
    section then only has Claude Code's /usage cache to go on.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param(
    [string]$ClaudeDir,
    [switch]$Autostart,
    [switch]$NoStart,
    [switch]$NoUsageStatusLine
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

# ---------- 1. Environment checks ----------
if ($env:OS -ne 'Windows_NT') { Fail 'Claude Status only runs on Windows.' }

$winPs = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $winPs)) { Fail "Windows PowerShell 5.1 was not found at $winPs." }

# Always run under Windows PowerShell 5.1 so settings.json is parsed/written the same way everywhere
# (PowerShell 7's ConvertFrom-Json rewrites date-like strings).
if ($PSVersionTable.PSEdition -eq 'Core') {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($ClaudeDir) { $a += @('-ClaudeDir', $ClaudeDir) }
    if ($Autostart) { $a += '-Autostart' }
    if ($NoStart)   { $a += '-NoStart' }
    if ($NoUsageStatusLine) { $a += '-NoUsageStatusLine' }
    & $winPs @a
    exit $LASTEXITCODE
}
Remove-TypeData System.Array -ErrorAction SilentlyContinue  # PS 5.1 ConvertTo-Json array quirk

$netfx = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
if (-not $netfx -or $netfx.Release -lt 378389) { Fail '.NET Framework 4.5 or later is required (it is included with Windows 10 and 11).' }

if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    Write-Warning "The 'claude' command was not found on PATH. That's fine if you only use the Claude Code VS Code extension; the hooks still apply."
}

$src = Join-Path $PSScriptRoot 'src'
$appFiles = @('hook.ps1', 'indicator.cs', 'indicator.ps1', 'start-indicator.vbs', 'stop-indicator.ps1', 'autostart.ps1', 'uninstall.ps1',
              'phone.cs', 'phone.html', 'phone.css', 'phone.js', 'phone-setup.ps1', 'usage.cs', 'statusline.ps1',
              'push.cs', 'sw.js')
foreach ($f in $appFiles + 'config.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $src $f))) { Fail "Missing source file src\$f. Run install.ps1 from the root of the Claude Status repository." }
}

if (-not $ClaudeDir) { $ClaudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' } }
$ClaudeDir    = [IO.Path]::GetFullPath($ClaudeDir)
$installDir   = Join-Path $ClaudeDir 'status-indicator'
$settingsPath = Join-Path $ClaudeDir 'settings.json'
$hookPath     = Join-Path $installDir 'hook.ps1'
$chainPath    = Join-Path $installDir 'statusline-chain.json'

$repoDir = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if ($repoDir -ieq $installDir -or $repoDir.StartsWith($installDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
    Fail "This repository is cloned into $installDir, which is where Claude Status gets installed. Clone it somewhere else (e.g. your Downloads folder) and run install.ps1 from there."
}

# ---------- JSON helpers ----------
function Read-JsonFile([string]$path) {
    $raw = [IO.File]::ReadAllText($path)
    if (-not $raw.Trim()) { return $null }
    return $raw | ConvertFrom-Json
}

function Format-JsonText([string]$json) {
    # Re-indents compact JSON with 2 spaces (PS 5.1's ConvertTo-Json output is hard to read).
    $sb = New-Object System.Text.StringBuilder
    $indent = 0; $inStr = $false; $esc = $false
    for ($i = 0; $i -lt $json.Length; $i++) {
        $c = $json[$i]
        if ($inStr) {
            [void]$sb.Append($c)
            if ($esc) { $esc = $false } elseif ($c -eq [char]'\') { $esc = $true } elseif ($c -eq [char]'"') { $inStr = $false }
            continue
        }
        if ($c -eq [char]'"') { $inStr = $true; [void]$sb.Append($c); continue }
        if ($c -eq [char]'{' -or $c -eq [char]'[') {
            $close = if ($c -eq [char]'{') { [char]'}' } else { [char]']' }
            if ($i + 1 -lt $json.Length -and $json[$i + 1] -eq $close) { [void]$sb.Append($c).Append($close); $i++; continue }
            $indent++; [void]$sb.Append($c).Append("`n").Append('  ' * $indent); continue
        }
        if ($c -eq [char]'}' -or $c -eq [char]']') { $indent--; [void]$sb.Append("`n").Append('  ' * $indent).Append($c); continue }
        if ($c -eq [char]',') { [void]$sb.Append(",`n").Append('  ' * $indent); continue }
        if ($c -eq [char]':') { [void]$sb.Append(': '); continue }
        if ([char]::IsWhiteSpace($c)) { continue }
        [void]$sb.Append($c)
    }
    return $sb.ToString() + "`n"
}

function Write-JsonFile([string]$path, $obj) {
    $text = Format-JsonText ($obj | ConvertTo-Json -Depth 100 -Compress)
    [IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))
}

function Test-ClaudeStatusHook($h) {
    if ($null -eq $h) { return $false }
    $parts = @()
    if ($h.PSObject.Properties['command']) { $parts += [string]$h.command }
    if ($h.PSObject.Properties['args'])    { $parts += @($h.args | ForEach-Object { [string]$_ }) }
    return (($parts -join ' ') -match '[\\/]status-indicator[\\/]hook\.ps1')
}

function Test-ClaudeStatusStatusLine($sl) {
    return ($null -ne $sl -and $null -ne $sl.PSObject.Properties['command'] -and ([string]$sl.command -match '[\\/]status-indicator[\\/]statusline\.ps1'))
}

# Removes every Claude Status hook entry; leaves all other hooks/groups untouched.
function Remove-ClaudeStatusHooks($settings) {
    $prop = $settings.PSObject.Properties['hooks']
    if (-not $prop -or $null -eq $prop.Value) { return }
    $hooks = $prop.Value
    $removedAny = $false
    foreach ($evt in @($hooks.PSObject.Properties.Name)) {
        $groups = $hooks.$evt
        if ($groups -isnot [array]) { continue }
        $kept = @(); $changed = $false
        foreach ($g in $groups) {
            if ($null -ne $g -and $g.PSObject.Properties['hooks'] -and $g.hooks -is [array]) {
                $rest = @($g.hooks | Where-Object { -not (Test-ClaudeStatusHook $_) })
                if ($rest.Count -ne $g.hooks.Count) {
                    $changed = $true
                    if ($rest.Count -eq 0) { continue }
                    $g.hooks = $rest
                }
            }
            $kept += $g
        }
        if ($changed) {
            $removedAny = $true
            if ($kept.Count -gt 0) { $hooks.$evt = $kept } else { $hooks.PSObject.Properties.Remove($evt) }
        }
    }
    if ($removedAny -and @($hooks.PSObject.Properties).Count -eq 0) { $settings.PSObject.Properties.Remove('hooks') }
}

# ---------- 2. Read settings.json BEFORE changing anything ----------
$settings = $null
if (Test-Path -LiteralPath $settingsPath) {
    try { $settings = Read-JsonFile $settingsPath }
    catch { Fail "$settingsPath is not valid JSON, so it was left untouched. Fix it (or move it aside) and run install.ps1 again. Nothing was installed." }
    if ($null -ne $settings -and $settings -isnot [System.Management.Automation.PSCustomObject]) { Fail "$settingsPath does not contain a JSON object. Nothing was installed." }
}
if ($null -eq $settings) { $settings = New-Object PSObject }

# ---------- 3. Install application files ----------
Write-Host "Installing Claude Status to $installDir"
$wasRunning = $false
if (Test-Path -LiteralPath (Join-Path $installDir 'indicator.ps1')) {
    $target = Join-Path $installDir 'indicator.ps1'
    $running = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($target, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
    foreach ($p in $running) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue; $wasRunning = $true }
    if ($wasRunning) { Write-Host '  stopped the running indicator so the update takes effect' }
}

$null = New-Item -ItemType Directory -Force -Path (Join-Path $installDir 'state\sessions')
foreach ($f in $appFiles) {
    Copy-Item -LiteralPath (Join-Path $src $f) -Destination (Join-Path $installDir $f) -Force
}

# config.json: keep the user's existing choices, only add settings introduced by newer versions.
$cfgDest = Join-Path $installDir 'config.json'
if (-not (Test-Path -LiteralPath $cfgDest)) {
    Copy-Item -LiteralPath (Join-Path $src 'config.json') -Destination $cfgDest
} else {
    try {
        $userCfg = Read-JsonFile $cfgDest
        $defCfg  = Read-JsonFile (Join-Path $src 'config.json')
        $added = @()
        foreach ($p in $defCfg.PSObject.Properties) {
            if (-not $userCfg.PSObject.Properties[$p.Name]) { $userCfg | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value; $added += $p.Name }
        }
        if ($added.Count -gt 0) { Write-JsonFile $cfgDest $userCfg; Write-Host "  config.json kept; added new options: $($added -join ', ')" }
        else { Write-Host '  config.json kept (your existing settings)' }
    } catch {
        Write-Warning "Existing config.json could not be read; left it unchanged."
    }
}
Get-ChildItem -LiteralPath $installDir -File | Unblock-File -ErrorAction SilentlyContinue

# ---------- 4. Merge hooks into settings.json ----------
function New-HookGroup([string]$matcher, [string]$action, [bool]$async) {
    $hook = [ordered]@{
        type    = 'command'
        command = 'powershell.exe'
        args    = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $hookPath, $action)
        timeout = 10
    }
    if ($async) { $hook['async'] = $true }
    $group = [ordered]@{}
    if ($matcher) { $group['matcher'] = $matcher }
    $group['hooks'] = @([pscustomobject]$hook)
    return [pscustomobject]$group
}

# event, matcher, action, async
$plan = @(
    @('UserPromptSubmit',   '',                                                                          'working',   $false),
    @('Stop',               '',                                                                          'done',      $false),
    @('StopFailure',        '',                                                                          'error',     $false),
    @('Notification',       'permission_prompt|elicitation_dialog|elicitation_url_dialog|agent_needs_input', 'needs_you', $false),
    @('PermissionRequest',  '',                                                                          'needs_you', $true),
    @('PreToolUse',         'AskUserQuestion',                                                           'needs_you', $true),
    @('PostToolUse',        '',                                                                          'resume',    $true),
    @('PostToolUseFailure', '',                                                                          'resume',    $true),
    @('SessionEnd',         '',                                                                          'end',       $false)
)

$before = $settings | ConvertTo-Json -Depth 100 -Compress
Remove-ClaudeStatusHooks $settings   # drop any previous/outdated Claude Status entries so nothing is duplicated
if (-not $settings.PSObject.Properties['hooks'] -or $null -eq $settings.hooks) {
    if ($settings.PSObject.Properties['hooks']) { $settings.PSObject.Properties.Remove('hooks') }
    $settings | Add-Member -NotePropertyName hooks -NotePropertyValue (New-Object PSObject)
}
foreach ($p in $plan) {
    $evt = $p[0]
    $group = New-HookGroup $p[1] $p[2] $p[3]
    if ($settings.hooks.PSObject.Properties[$evt]) { $settings.hooks.$evt = @(@($settings.hooks.$evt) + $group) }
    else { $settings.hooks | Add-Member -NotePropertyName $evt -NotePropertyValue @($group) }
}

# ---------- 4b. Status line feed for the Claude Usage section ----------
# Claude Code passes the documented rate_limits data only to a status line command, so statusline.ps1
# becomes the status line. A status line you already had is saved to statusline-chain.json: statusline.ps1
# runs it and shows its output, and uninstall.ps1 puts it back.
$slProp = $settings.PSObject.Properties['statusLine']
$currentSl = if ($slProp) { $slProp.Value } else { $null }
$slNote = ''
if ($NoUsageStatusLine) {
    if (Test-ClaudeStatusStatusLine $currentSl) {
        if (Test-Path -LiteralPath $chainPath) { $settings.statusLine = Read-JsonFile $chainPath; Remove-Item -LiteralPath $chainPath -Force; $slNote = 'removed the usage status line and restored your previous one' }
        else { $settings.PSObject.Properties.Remove('statusLine'); $slNote = 'removed the usage status line' }
    }
} else {
    if ($null -ne $currentSl -and -not (Test-ClaudeStatusStatusLine $currentSl)) {
        Write-JsonFile $chainPath $currentSl
        $slNote = 'your existing status line keeps running (saved as statusline-chain.json; uninstall restores it)'
    } elseif ($null -eq $currentSl -and (Test-Path -LiteralPath $chainPath)) {
        Remove-Item -LiteralPath $chainPath -Force   # the status line was removed since: don't bring the old one back
    }
    $slCmd = 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ((Join-Path $installDir 'statusline.ps1').Replace('\', '/')) + '"'
    $sl = [ordered]@{ type = 'command'; command = $slCmd }
    # keep the display options (padding, refreshInterval, ...) of the status line being wrapped
    $base = if (Test-ClaudeStatusStatusLine $currentSl) { $currentSl } elseif (Test-Path -LiteralPath $chainPath) { Read-JsonFile $chainPath } else { $null }
    if ($base) { foreach ($p in $base.PSObject.Properties) { if ($p.Name -ne 'type' -and $p.Name -ne 'command') { $sl[$p.Name] = $p.Value } } }
    if ($slProp) { $settings.statusLine = [pscustomobject]$sl } else { $settings | Add-Member -NotePropertyName statusLine -NotePropertyValue ([pscustomobject]$sl) }
}
$after = $settings | ConvertTo-Json -Depth 100 -Compress

$backup = $null
if ($before -ceq $after) {
    Write-Host "  hooks and status line already up to date in $settingsPath (not modified)"
} else {
    $null = New-Item -ItemType Directory -Force -Path $ClaudeDir
    if (Test-Path -LiteralPath $settingsPath) {
        $backup = $settingsPath + '.bak-claude-status-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
        Copy-Item -LiteralPath $settingsPath -Destination $backup
        Write-Host "  backed up settings.json to $backup"
    }
    Write-JsonFile $settingsPath $settings
    Write-Host "  merged $($plan.Count) Claude Status hooks into $settingsPath"
}
if ($slNote) { Write-Host "  $slNote" }

# ---------- 5. Autostart / start ----------
if ($Autostart) { & $winPs -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installDir 'autostart.ps1') -Enable | ForEach-Object { "  $_" } }

$started = $false
if (-not $NoStart) {
    Start-Process -FilePath (Join-Path $env:WINDIR 'System32\wscript.exe') -ArgumentList ('"' + (Join-Path $installDir 'start-indicator.vbs') + '"')
    $started = $true
}

Write-Host ''
Write-Host 'Claude Status installed successfully.' -ForegroundColor Green
Write-Host "  App folder : $installDir"
Write-Host "  Hooks      : $settingsPath"
if ($backup) { Write-Host "  Backup     : $backup" }
if ($started) { Write-Host '  Indicator  : started (look near the bottom-right of your screen)' }
else          { Write-Host "  Indicator  : start it by double-clicking $(Join-Path $installDir 'start-indicator.vbs')" }
Write-Host ''
Write-Host 'Claude Code sessions that are already open may need /hooks (or a restart) to pick up the hooks.'
Write-Host 'Prompts and Claude messages are never stored; only state, event, project folder name, times and the Claude process ID.'
exit 0

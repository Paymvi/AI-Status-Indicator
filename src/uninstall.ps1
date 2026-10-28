<#
.SYNOPSIS
    Completely removes Claude Status for the current Windows user.

.DESCRIPTION
    1. Stops the indicator.  2. Removes its autostart shortcut (if any).
    3. Removes ONLY Claude Status hook entries from settings.json (backup made first).
    4. Deletes the installed %USERPROFILE%\.claude\status-indicator\ folder.
    Never deletes your cloned repository, Claude Code, the .claude folder, or unrelated settings/hooks.
    Self-contained, so it works even if other Claude Status files are already missing.

.PARAMETER ClaudeDir
    Claude Code's user config directory. Defaults to $env:CLAUDE_CONFIG_DIR if set, else %USERPROFILE%\.claude.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\.claude\status-indicator\uninstall.ps1"
#>
[CmdletBinding()]
param([string]$ClaudeDir)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

if ($env:OS -ne 'Windows_NT') { Fail 'Claude Status only runs on Windows.' }

# Run under Windows PowerShell 5.1 for consistent settings.json handling (see install.ps1).
$winPs = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
if ($PSVersionTable.PSEdition -eq 'Core' -and (Test-Path -LiteralPath $winPs)) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($ClaudeDir) { $a += @('-ClaudeDir', $ClaudeDir) }
    & $winPs @a
    exit $LASTEXITCODE
}
Remove-TypeData System.Array -ErrorAction SilentlyContinue

if (-not $ClaudeDir) { $ClaudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' } }
$ClaudeDir    = [IO.Path]::GetFullPath($ClaudeDir)
$installDir   = Join-Path $ClaudeDir 'status-indicator'
$settingsPath = Join-Path $ClaudeDir 'settings.json'

# ---------- JSON helpers (same as install.ps1) ----------
function Read-JsonFile([string]$path) {
    $raw = [IO.File]::ReadAllText($path)
    if (-not $raw.Trim()) { return $null }
    return $raw | ConvertFrom-Json
}

function Format-JsonText([string]$json) {
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

# ---------- Read settings first, so a broken file stops us before anything is removed ----------
$settings = $null
if (Test-Path -LiteralPath $settingsPath) {
    try { $settings = Read-JsonFile $settingsPath }
    catch { Fail "$settingsPath is not valid JSON, so nothing was removed. Fix it and run uninstall again." }
}

Write-Host "Uninstalling Claude Status from $installDir"

# ---------- 1. Stop the indicator ----------
$target = Join-Path $installDir 'indicator.ps1'
$procs = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($target, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
Write-Host "  stopped $($procs.Count) indicator process(es)"

# ---------- 1b. Phone dashboard: remove its `tailscale serve` entry (only one pointing at this dashboard) ----------
$phoneSetup = Join-Path $installDir 'phone-setup.ps1'
if (Test-Path -LiteralPath $phoneSetup) {
    try { & $phoneSetup -Disable | ForEach-Object { "  $_" } } catch { Write-Warning "Couldn't check tailscale serve for the phone dashboard: $_" }
}

# ---------- 2. Autostart shortcut (only if it points at this install) ----------
$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'Claude Status Indicator.lnk'
if (Test-Path -LiteralPath $lnk) {
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    if (($sc.Arguments + ' ' + $sc.TargetPath).IndexOf($installDir, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Remove-Item -LiteralPath $lnk -Force
        Write-Host '  removed autostart shortcut'
    } else {
        Write-Host "  left autostart shortcut alone (it points somewhere else): $lnk"
    }
} else {
    Write-Host '  no autostart shortcut'
}

# ---------- 3. Remove only Claude Status hooks ----------
if ($null -ne $settings) {
    $before = $settings | ConvertTo-Json -Depth 100 -Compress
    Remove-ClaudeStatusHooks $settings
    $after = $settings | ConvertTo-Json -Depth 100 -Compress
    if ($before -ceq $after) {
        Write-Host "  no Claude Status hooks found in $settingsPath"
    } else {
        $backup = $settingsPath + '.bak-claude-status-uninstall-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
        Copy-Item -LiteralPath $settingsPath -Destination $backup
        Write-JsonFile $settingsPath $settings
        Write-Host "  removed Claude Status hooks from $settingsPath (backup: $backup)"
    }
} else {
    Write-Host "  no settings.json at $settingsPath"
}

# ---------- 4. Delete the installed app folder (with guards) ----------
if (Test-Path -LiteralPath $installDir) {
    $item = Get-Item -LiteralPath $installDir -Force
    $isLink = [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
    # Refuse if a source repository is anywhere inside (e.g. someone cloned into this folder).
    $containsRepo = $false
    if (-not $isLink) {
        $containsRepo = [bool](Get-ChildItem -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq '.git' -or $_.Name -eq 'install.ps1' } | Select-Object -First 1)
    }
    if ((Split-Path -Leaf $installDir) -ne 'status-indicator' -or $isLink -or $containsRepo) {
        Write-Warning "Not deleting $installDir because it is a link or contains a source repository, not just an installed copy. Delete it manually if you're sure."
    } else {
        Set-Location -LiteralPath $env:USERPROFILE   # don't hold the folder open
        Remove-Item -LiteralPath $installDir -Recurse -Force
        Write-Host "  deleted $installDir"
    }
} else {
    Write-Host '  app folder already gone'
}

Write-Host ''
Write-Host 'Claude Status has been uninstalled.' -ForegroundColor Green
Write-Host 'Your Claude Code settings, other hooks, and any cloned Claude Status repository were not touched.'
exit 0

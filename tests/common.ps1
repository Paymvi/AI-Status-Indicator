# Shared setup for run-tests.ps1 and live-tests.ps1 (dot-sourced). Set $tmp before dot-sourcing.
$ErrorActionPreference = 'Stop'
Remove-TypeData System.Array -ErrorAction SilentlyContinue

$winPs = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo 'src'
if (-not $tmp) { $tmp = Join-Path ([IO.Path]::GetTempPath()) ('claude-status-tests-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$null = New-Item -ItemType Directory -Force $tmp

$script:pass = 0; $script:fail = 0; $script:skip = 0; $script:failures = @()
# When $script:ResultFile is set (live-tests.ps1 running hidden), results are also written there for run-tests.ps1.
function Report([string]$kind, [string]$name, [string]$info) {
    if ($script:ResultFile) { Add-Content -LiteralPath $script:ResultFile -Value ($kind + "`t" + $name + "`t" + $info) }
}
# The hidden live-tests.ps1 process gives up its console (as the indicator does), so it must not write to it.
function Say([string]$text, [string]$color = 'Gray') { if (-not $script:ResultFile) { Write-Host $text -ForegroundColor $color } }
function Check([string]$name, $cond, [string]$info = '') {
    Report $(if ($cond) { 'PASS' } else { 'FAIL' }) $name $info
    if ($cond) { $script:pass++; Say "  PASS  $name" Green }
    else { $script:fail++; $script:failures += $name; Say "  FAIL  $name  $info" Red }
}
function Skip([string]$name, [string]$why) { Report 'SKIP' $name $why; $script:skip++; Say "  SKIP  $name ($why)" Yellow }
function Info([string]$text) { Report 'INFO' $text ''; Say "  info  $text" }
function Section([string]$t) { Report 'SECTION' $t ''; Say "`n== $t" Cyan }

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Path (Join-Path $src 'indicator.cs'), (Join-Path $src 'phone.cs'), (Join-Path $src 'usage.cs'), (Join-Path $src 'push.cs') -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Web.Extensions, System.Core, System.Security, System.Numerics
Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class TNative {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public static uint WindowPid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
    // Test oracle only (the product never reads window titles): a terminal window shows its tab's title.
    public static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
}
'@
[void][TNative]::SetProcessDPIAware()   # same DPI mode as the real indicator

function StartOf([int]$procId) { (Get-Process -Id $procId).StartTime.ToUniversalTime() }
function Iso([DateTime]$t) { $t.ToUniversalTime().ToString('o') }

# A temporary install-like folder (config + state\sessions) for an IndicatorForm.
function New-Root([hashtable]$cfgOverride = @{}, [switch]$NoConfig) {
    $root = Join-Path $tmp ('ind-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $null = New-Item -ItemType Directory -Force (Join-Path $root 'state\sessions')
    if (-not $NoConfig) {
        $c = Get-Content (Join-Path $src 'config.json') -Raw | ConvertFrom-Json
        foreach ($k in $cfgOverride.Keys) { if ($c.PSObject.Properties[$k]) { $c.$k = $cfgOverride[$k] } else { $c | Add-Member -NotePropertyName $k -NotePropertyValue $cfgOverride[$k] } }
        [IO.File]::WriteAllText((Join-Path $root 'config.json'), ($c | ConvertTo-Json -Depth 5))
    }
    return $root
}

# Writes a state file the way hook.ps1 does (v2), or an old v1 file with -v 1.
function Put([string]$root, [string]$id, [string]$st, [string]$proj, [int]$sinceSec = 60, [int]$firstMin = 10, [int]$cpid = 0, $cstart = $null, [int]$v = 2, [int]$updSec = -1) {
    $n = [DateTime]::UtcNow
    $upd = if ($updSec -ge 0) { $n.AddSeconds(-$updSec) } else { $n.AddSeconds(-$sinceSec) }
    if ($v -eq 1) { $o = [ordered]@{ v = 1; session_id = $id; state = $st; event = 'x'; detail = ''; project = $proj; updated = (Iso $upd) } }
    else {
        $o = [ordered]@{ v = 2; session_id = $id; state = $st; event = 'x'; detail = ''; project = $proj; updated = (Iso $upd); state_since = (Iso $n.AddSeconds(-$sinceSec))
            first_seen = (Iso $n.AddMinutes(-$firstMin)); claude_pid = $cpid; claude_started = $(if ($cstart) { Iso $cstart } else { '' }) }
    }
    [IO.File]::WriteAllText((Join-Path $root "state\sessions\$id.json"), ($o | ConvertTo-Json -Compress))
}

function Form([string]$root, [single]$scale = 1) { New-Object ClaudeStatus.IndicatorForm($root, $scale, $true) }
function Ids($f) { ($f.Workers | ForEach-Object { $_.Id }) -join ',' }

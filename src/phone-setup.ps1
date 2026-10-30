<#
.SYNOPSIS
    Turns the Claude Status phone dashboard on or off and makes it reachable from your phone over Tailscale.

.DESCRIPTION
    (no switch)  Turns on "phoneDashboard" in config.json (the running indicator starts it within seconds;
                 the indicator is started if it isn't running), then publishes it to your tailnet only with
                     tailscale serve --bg --http=80 http://127.0.0.1:<phonePort>
                 and prints the address to open on your phone.
    -Status      Shows the current state. Changes nothing.
    -Disable     Removes that `tailscale serve` entry (only if it points at Claude Status) and turns the
                 dashboard off. The desktop app keeps running.
    -NoServe     Only turns the dashboard on in config.json; doesn't touch Tailscale.
    -Https       Serves it over HTTPS instead (tailscale serve --bg --https=443 ...), which phone notifications
                 need. Requires HTTPS Certificates to be enabled for your tailnet in the Tailscale admin console;
                 the certificate puts this computer's tailnet name (<machine>.<tailnet>.ts.net) in public
                 Certificate Transparency logs. Access stays tailnet-only. An existing HTTP entry is kept.
    -HttpPort    The tailnet port to serve on (default 80, or 443 with -Https).

    Never uses Tailscale Funnel: only devices signed in to your own tailnet can reach the dashboard. It also
    refuses to report success if Funnel is found switched on for the dashboard's port.
    The dashboard itself listens on 127.0.0.1 only; `tailscale serve` is the only way in from the phone.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\.claude\status-indicator\phone-setup.ps1"
#>
[CmdletBinding()]
param([switch]$Disable, [switch]$Status, [switch]$NoServe, [switch]$Https, [ValidateRange(1, 65535)][int]$HttpPort = 80)
if ($Https -and -not $PSBoundParameters.ContainsKey('HttpPort')) { $HttpPort = 443 }

# Run under Windows PowerShell 5.1 so config.json is read/written the same way as by install.ps1.
$winPs = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
if ($PSVersionTable.PSEdition -eq 'Core' -and (Test-Path -LiteralPath $winPs)) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-HttpPort', $HttpPort)
    if ($Disable) { $a += '-Disable' }; if ($Status) { $a += '-Status' }; if ($NoServe) { $a += '-NoServe' }; if ($Https) { $a += '-Https' }
    & $winPs @a
    exit $LASTEXITCODE
}
Remove-TypeData System.Array -ErrorAction SilentlyContinue
$ErrorActionPreference = 'Stop'

function Fail([string]$msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }
function Note([string]$msg) { Write-Host "  $msg" }

$root = $PSScriptRoot
$cfgPath = Join-Path $root 'config.json'
if (-not (Test-Path -LiteralPath (Join-Path $root 'phone.cs'))) { Fail "Run this from the installed Claude Status folder (normally $env:USERPROFILE\.claude\status-indicator)." }

# ---------- config.json (same formatting as install.ps1) ----------
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

function Read-Config {
    if (-not (Test-Path -LiteralPath $cfgPath)) { return New-Object PSObject }
    try { $c = [IO.File]::ReadAllText($cfgPath) | ConvertFrom-Json } catch { Fail "$cfgPath contains an error, so nothing was changed. Fix it and try again." }
    if ($null -eq $c) { return New-Object PSObject }
    return $c
}

function Set-PhoneEnabled([bool]$on) {
    $c = Read-Config
    if ($c.PSObject.Properties['phoneDashboard'] -and $c.phoneDashboard -eq $on) { return $false }
    if ($c.PSObject.Properties['phoneDashboard']) { $c.phoneDashboard = $on } else { $c | Add-Member -NotePropertyName phoneDashboard -NotePropertyValue $on }
    $tmp = $cfgPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($tmp, (Format-JsonText ($c | ConvertTo-Json -Depth 20 -Compress)), (New-Object System.Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $cfgPath) { [IO.File]::Replace($tmp, $cfgPath, [NullString]::Value) } else { [IO.File]::Move($tmp, $cfgPath) }
    return $true
}

$cfg = Read-Config
$port = 48521
if ($cfg.PSObject.Properties['phonePort'] -and [int]$cfg.phonePort -ge 1024 -and [int]$cfg.phonePort -le 65535) { $port = [int]$cfg.phonePort }
$enabled = [bool]($cfg.PSObject.Properties['phoneDashboard'] -and $cfg.phoneDashboard -eq $true)
$target = "http://127.0.0.1:$port"
# Written when this script adds the serve entry, so -Disable (also run by uninstall.ps1) only ever removes
# an entry this install created.
$marker = Join-Path $root 'state\phone-serve.txt'

# ---------- local checks ----------
function Test-IndicatorRunning {
    $script = Join-Path $root 'indicator.ps1'
    return [bool](Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($script, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

# Asks the dashboard for its board on 127.0.0.1 (this computer only). Returns the HTTP status, or 0.
function Test-Dashboard {
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $c.ReceiveTimeout = 3000
        $c.Connect('127.0.0.1', $port)
        $s = $c.GetStream()
        $b = [Text.Encoding]::ASCII.GetBytes("GET /api/board HTTP/1.1`r`nHost: 127.0.0.1:$port`r`n`r`n")
        $s.Write($b, 0, $b.Length)
        $line = (New-Object IO.StreamReader($s)).ReadLine()
        $c.Close()
        if ($line -match '^HTTP/1\.1 (\d{3})') { return [int]$Matches[1] }
    } catch {}
    return 0
}

# ---------- Tailscale ----------
$ts = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
if (-not (Test-Path -LiteralPath $ts)) { $cmd = Get-Command tailscale -ErrorAction SilentlyContinue; $ts = if ($cmd) { $cmd.Source } else { $null } }

function Invoke-Tailscale([string[]]$argList) {
    $ErrorActionPreference = 'Continue'
    $out = & $ts @argList 2>&1 | ForEach-Object { "$_" }
    return @{ Code = $LASTEXITCODE; Out = ($out -join "`n").Trim() }
}

function Get-TailnetSelf {
    if (-not $ts) { return $null }
    $r = Invoke-Tailscale @('status', '--json', '--peers=false')
    if ($r.Code -ne 0) { return $null }
    try { $j = $r.Out | ConvertFrom-Json } catch { return $null }
    $dns = ([string]$j.Self.DNSName).TrimEnd('.')
    return @{ State = [string]$j.BackendState; Dns = $dns; Short = $(if ($dns) { $dns.Split('.')[0] } else { '' }) }
}

# The current `tailscale serve` web handlers: one entry per (port, path) with its proxy target.
function Get-ServeEntries {
    $list = @()
    if (-not $ts) { return $list }
    $r = Invoke-Tailscale @('serve', 'status', '--json')
    if ($r.Code -ne 0 -or -not $r.Out) { return $list }
    try { $j = $r.Out | ConvertFrom-Json } catch { return $list }
    if (-not $j -or -not $j.PSObject.Properties['Web']) { return $list }
    foreach ($site in $j.Web.PSObject.Properties) {
        $p = [int]($site.Name -replace '^.*:', '')
        $https = $false
        if ($j.PSObject.Properties['TCP'] -and $j.TCP.PSObject.Properties["$p"] -and $j.TCP."$p".PSObject.Properties['HTTPS']) { $https = [bool]$j.TCP."$p".HTTPS }
        foreach ($h in $site.Value.Handlers.PSObject.Properties) {
            $list += [pscustomobject]@{ Port = $p; Path = $h.Name; Proxy = [string]$h.Value.Proxy; Https = $https }
        }
    }
    return $list
}

# Ports where Tailscale Funnel (public internet access) is switched on, from `tailscale serve status --json`.
function Get-FunnelPorts {
    $ports = @()
    if (-not $ts) { return $ports }
    $r = Invoke-Tailscale @('serve', 'status', '--json')
    if ($r.Code -ne 0 -or -not $r.Out) { return $ports }
    try { $j = $r.Out | ConvertFrom-Json } catch { return $ports }
    if ($j -and $j.PSObject.Properties['AllowFunnel'] -and $j.AllowFunnel) {
        foreach ($f in $j.AllowFunnel.PSObject.Properties) { if ($f.Value -eq $true) { $ports += [int]($f.Name -replace '^.*:', '') } }
    }
    return $ports
}

function Test-Ours($e) { return @("http://127.0.0.1:$port", "http://127.0.0.1:$port/", "http://localhost:$port", "http://localhost:$port/") -contains $e.Proxy }

function Show-Url($self, [int]$p, [bool]$https) {
    $scheme = if ($https) { 'https' } else { 'http' }
    $suffix = if (($https -and $p -eq 443) -or (-not $https -and $p -eq 80)) { '' } else { ":$p" }
    Write-Host ''
    Write-Host 'Open this on your phone (with Tailscale connected):' -ForegroundColor Green
    Write-Host ("    {0}://{1}{2}/" -f $scheme, $self.Dns, $suffix) -ForegroundColor Green
    if ($self.Short) { Write-Host ("    (or the short form {0}://{1}{2}/ if MagicDNS short names work on your phone)" -f $scheme, $self.Short, $suffix) }
}

# ======================================================================================
if ($Status) {
    Write-Host 'Claude Status phone dashboard'
    Note ("config.json : phoneDashboard = {0}, phonePort = {1}" -f $enabled, $port)
    Note ("indicator   : {0}" -f $(if (Test-IndicatorRunning) { 'running' } else { 'NOT running' }))
    $code = Test-Dashboard
    Note ("dashboard   : {0}" -f $(if ($code -eq 200) { "answering on $target" } elseif ($code) { "answering with HTTP $code" } else { "not listening on $target" }))
    if (-not $ts) { Note 'tailscale   : not found'; exit 0 }
    $self = Get-TailnetSelf
    Note ("tailscale   : {0}{1}" -f $(if ($self) { $self.State } else { 'unknown' }), $(if ($self -and $self.Dns) { " as $($self.Dns)" } else { '' }))
    $ours = @(Get-ServeEntries | Where-Object { Test-Ours $_ })
    if ($ours.Count -eq 0) { Note 'serve       : no tailscale serve entry for the dashboard' }
    foreach ($e in $ours) { Note ("serve       : {0} port {1}{2} -> {3}" -f $(if ($e.Https) { 'HTTPS' } else { 'HTTP' }), $e.Port, $e.Path, $e.Proxy) }
    $funnel = @(Get-FunnelPorts)
    Note ("funnel      : {0}" -f $(if ($funnel.Count) { 'ON for port(s) ' + ($funnel -join ', ') + ' (public internet access!)' } else { 'off (tailnet only)' }))
    if ($ours.Count -gt 0 -and $self -and $self.Dns) { Show-Url $self $ours[0].Port $ours[0].Https }
    exit 0
}

if ($Disable) {
    Write-Host 'Turning off the Claude Status phone dashboard'
    if ($ts -and (Test-Path -LiteralPath $marker)) {
        foreach ($e in @(Get-ServeEntries | Where-Object { Test-Ours $_ })) {
            $flag = if ($e.Https) { "--https=$($e.Port)" } else { "--http=$($e.Port)" }
            $r = Invoke-Tailscale @('serve', $flag, 'off')
            if ($r.Code -eq 0) { Note "removed tailscale serve entry on port $($e.Port)" }
            else { Write-Warning "Couldn't remove the tailscale serve entry on port $($e.Port): $($r.Out)`nRun this yourself: tailscale serve $flag off" }
        }
        Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
    }
    if (Set-PhoneEnabled $false) { Note 'phoneDashboard = false in config.json (the indicator stops it within seconds)' } else { Note 'phoneDashboard was already off' }
    exit 0
}

# ---------- enable ----------
Write-Host 'Setting up the Claude Status phone dashboard'
if (Set-PhoneEnabled $true) { Note 'phoneDashboard = true in config.json' } else { Note 'phoneDashboard was already on' }
if (-not (Test-IndicatorRunning)) {
    Note 'the indicator was not running; starting it'
    Start-Process -FilePath (Join-Path $env:WINDIR 'System32\wscript.exe') -ArgumentList ('"' + (Join-Path $root 'start-indicator.vbs') + '"')
}
$code = 0
for ($i = 0; $i -lt 30 -and $code -ne 200; $i++) { $code = Test-Dashboard; if ($code -ne 200) { Start-Sleep -Milliseconds 500 } }
if ($code -eq 200) { Note "dashboard answering on $target (this computer only)" }
else { Write-Warning "The dashboard isn't answering on $target yet. Right-click the Claude Status tray icon: the menu shows 'Phone dashboard: ...' with the reason (for example, another program using port $port; then set a different phonePort in config.json and run this again)." }

if ($NoServe) { Note 'skipped Tailscale (-NoServe)'; exit 0 }
if (-not $ts) { Fail 'Tailscale was not found. Install it from the Tailscale website, sign in, and run this again.' }
$self = Get-TailnetSelf
if (-not $self -or $self.State -ne 'Running') { Fail "Tailscale isn't connected on this computer (state: $(if ($self) { $self.State } else { 'unknown' })). Open Tailscale, sign in / connect, and run this again." }
if (-not $self.Dns) { Fail "This computer has no MagicDNS name. Turn on MagicDNS in the Tailscale admin console (DNS page) and run this again." }

$entries = @(Get-ServeEntries)
$onPort = @($entries | Where-Object { $_.Port -eq $HttpPort })
$flag = if ($Https) { "--https=$HttpPort" } else { "--http=$HttpPort" }
if ($onPort | Where-Object { Test-Ours $_ -and $_.Path -eq '/' -and $_.Https -eq [bool]$Https }) {
    Note "tailscale serve already forwards tailnet port $HttpPort to the dashboard"
} elseif ($onPort.Count -gt 0) {
    Fail ("Tailscale serve already uses tailnet port $HttpPort for something else ({0}). Nothing was changed there. Pick another port, e.g.: phone-setup.ps1 -HttpPort 8080" -f (($onPort | ForEach-Object { "$($_.Path) -> $($_.Proxy)" }) -join ', '))
} else {
    $r = Invoke-Tailscale @('serve', '--bg', $flag, $target)
    if ($r.Code -ne 0) {
        Write-Host $r.Out
        $hint = if ($Https) { "`nFor HTTPS, HTTPS Certificates must be enabled for your tailnet (Tailscale admin console, DNS page)." } else { '' }
        Fail ("tailscale serve failed. Run this yourself (an administrator prompt may be needed), then open the address below:`n    tailscale serve --bg $flag $target$hint")
    }
    Note "tailscale serve: tailnet port $HttpPort -> $target (your tailnet only; not Funnel, not the internet)"
}
$null = New-Item -ItemType Directory -Force -Path (Split-Path $marker)
[IO.File]::WriteAllText($marker, "tailscale serve $flag -> $target`n")
if (@(Get-FunnelPorts) -contains $HttpPort) {
    Fail "Tailscale Funnel is ON for port $HttpPort, so the dashboard is reachable from the public internet. Turn it off with:  tailscale funnel $flag off"
}
Note "funnel: off for port $HttpPort (tailnet only)"
Show-Url $self $HttpPort ([bool]$Https)
Write-Host ''
Write-Host 'To turn it off again:  phone-setup.ps1 -Disable'
exit 0

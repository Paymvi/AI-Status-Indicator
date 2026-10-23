# Stops the Claude Status indicator started from this folder (does not affect Claude Code or its hooks).
param([string]$InstallDir = $PSScriptRoot)
$target = Join-Path $InstallDir 'indicator.ps1'
$procs = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($target, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
"Stopped $($procs.Count) indicator process(es)."

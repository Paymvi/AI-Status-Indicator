# Claude Status Indicator - launcher. Compiles indicator.cs (+ phone.cs, the optional phone dashboard) with the
# built-in .NET Framework compiler and runs it.
# Usually started via start-indicator.vbs (no console window). Only one instance runs at a time.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Add-Type -Path (Join-Path $root 'indicator.cs'), (Join-Path $root 'phone.cs') -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Web.Extensions
[ClaudeStatus.IndicatorForm]::Run($root)

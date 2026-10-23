# Enable/disable starting the Claude Status Indicator when you sign in to Windows.
#   powershell -ExecutionPolicy Bypass -File autostart.ps1 -Enable
#   powershell -ExecutionPolicy Bypass -File autostart.ps1 -Disable
param([switch]$Enable, [switch]$Disable)
$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'Claude Status Indicator.lnk'
if ($Enable) {
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $sc.TargetPath = Join-Path $env:WINDIR 'System32\wscript.exe'
    $sc.Arguments = '"' + (Join-Path $PSScriptRoot 'start-indicator.vbs') + '"'
    $sc.WorkingDirectory = $PSScriptRoot
    $sc.Description = 'Claude Status Indicator'
    $sc.Save()
    "Autostart enabled: $lnk"
} elseif ($Disable) {
    Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue
    "Autostart disabled."
} else {
    "Autostart is currently " + $(if (Test-Path -LiteralPath $lnk) { 'ENABLED' } else { 'disabled' }) + ". Use -Enable or -Disable."
}

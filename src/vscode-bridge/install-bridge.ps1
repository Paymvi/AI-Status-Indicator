# Claude Status Bridge - packages this folder as a .vsix and installs it with VS Code's own command line
# (code --install-extension). No npm, no vsce, nothing downloaded. Safe to run again (it reinstalls the same version).
#   powershell -ExecutionPolicy Bypass -File install-bridge.ps1            install / update
#   powershell -ExecutionPolicy Bypass -File install-bridge.ps1 -Uninstall remove it from VS Code
#   powershell -ExecutionPolicy Bypass -File install-bridge.ps1 -PackageOnly <path.vsix>   just build the .vsix
#   -From <folder> packages another extension folder (the tests use it for their driver)
param([switch]$Uninstall, [string]$PackageOnly = '', [string]$From = '')
$ErrorActionPreference = 'Stop'
$here = if ($From) { $From } else { $PSScriptRoot }
$pkg = Get-Content -LiteralPath (Join-Path $here 'package.json') -Raw | ConvertFrom-Json
$id = "$($pkg.publisher).$($pkg.name)"

function Find-Code {
    $c = Get-Command code.cmd -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @("$env:LOCALAPPDATA\Programs\Microsoft VS Code\bin\code.cmd", "$env:ProgramFiles\Microsoft VS Code\bin\code.cmd")) { if (Test-Path -LiteralPath $p) { return $p } }
    return $null
}

function New-Vsix([string]$out) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011" xmlns:d="http://schemas.microsoft.com/developer/vsx-schema-design/2011">
  <Metadata>
    <Identity Language="en-US" Id="$($pkg.name)" Version="$($pkg.version)" Publisher="$($pkg.publisher)" />
    <DisplayName>$($pkg.displayName)</DisplayName>
    <Description xml:space="preserve">$([Security.SecurityElement]::Escape($pkg.description))</Description>
    <Categories>Other</Categories>
    <Properties>
      <Property Id="Microsoft.VisualStudio.Code.Engine" Value="$($pkg.engines.vscode)" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionKind" Value="ui" />
    </Properties>
  </Metadata>
  <Installation><InstallationTarget Id="Microsoft.VisualStudio.Code" /></Installation>
  <Dependencies />
  <Assets><Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" /></Assets>
</PackageManifest>
"@
    $types = '<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension=".json" ContentType="application/json" /><Default Extension=".js" ContentType="application/javascript" /><Default Extension=".vsixmanifest" ContentType="text/xml" /></Types>'
    $zip = [IO.Compression.ZipFile]::Open($out, 'Create')
    try {
        $put = {
            param($name, [byte[]]$bytes)
            $e = $zip.CreateEntry($name)   # forward slashes: what VS Code expects
            $s = $e.Open(); try { $s.Write($bytes, 0, $bytes.Length) } finally { $s.Dispose() }
        }
        $utf8 = New-Object Text.UTF8Encoding $false
        & $put '[Content_Types].xml' $utf8.GetBytes($types)
        & $put 'extension.vsixmanifest' $utf8.GetBytes($manifest)
        foreach ($f in 'package.json', 'extension.js') { & $put "extension/$f" ([IO.File]::ReadAllBytes((Join-Path $here $f))) }
    } finally { $zip.Dispose() }
}

if ($PackageOnly) { New-Vsix $PackageOnly; Write-Output "Built $PackageOnly"; exit 0 }

$code = Find-Code
if (-not $code) { Write-Output 'VS Code (code.cmd) wasn''t found. Install VS Code, then run this again.'; exit 2 }
# code.cmd is a batch file: every argument here is a fixed flag, the extension id, or a path in %TEMP% we created.
if ($Uninstall) {
    & { $ErrorActionPreference = 'Continue'; & $code --uninstall-extension $id 2>&1 | ForEach-Object { "$_" } }   # code.cmd's Node warnings go to stderr
    exit 0
}
$vsix = Join-Path ([IO.Path]::GetTempPath()) ("claude-status-bridge-" + $pkg.version + ".vsix")
New-Vsix $vsix
$out = & { $ErrorActionPreference = 'Continue'; & $code --install-extension $vsix --force 2>&1 | ForEach-Object { "$_" } }   # code.cmd's Node warnings go to stderr
$ok = $LASTEXITCODE -eq 0
Remove-Item -LiteralPath $vsix -Force -ErrorAction SilentlyContinue
$out | Write-Output
if (-not $ok) { exit 1 }
Write-Output "Installed $id $($pkg.version). Open VS Code windows pick it up within a few seconds (if one doesn't, run 'Developer: Reload Window' there)."
exit 0

param([switch]$Settings)
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'dist/win-x64/Winisland.exe'
if (!(Test-Path -LiteralPath $executable)) { & (Join-Path $PSScriptRoot 'scripts/Build.ps1') }
if ($Settings) { Start-Process -FilePath $executable -ArgumentList '--settings' -WindowStyle Hidden }
else { Start-Process -FilePath $executable -ArgumentList '--background' -WindowStyle Hidden }

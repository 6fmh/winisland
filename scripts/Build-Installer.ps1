param(
    [switch]$Verify,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$CompilerPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ($Verify) {
    $hostRuntime = 'win-' + [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($Runtime -ne $hostRuntime) { throw "Installer verification requires the matching Windows architecture ($hostRuntime)." }
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$localCompiler = Join-Path $projectRoot '.tools/inno-setup/compiler/ISCC.exe'
if (!$CompilerPath) {
    $candidates = @(
        $localCompiler,
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe')
    )
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath)) {
    throw 'Install Inno Setup 6 from https://jrsoftware.org/isdl.php, then pass -CompilerPath to ISCC.exe.'
}
$CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
$installerDirectory = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'dist/installer' }
$publishDirectory = Join-Path $projectRoot "artifacts/installer/publish/$Runtime"
& (Join-Path $PSScriptRoot 'Build.ps1') -SelfContained -Runtime $Runtime -OutputDirectory $publishDirectory
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/Winisland/Winisland.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Expected a numeric installer version.' }
& $CompilerPath "/DPublishDirectory=$publishDirectory" "/DInstallerOutputDirectory=$installerDirectory" "/DAppVersion=$version" "/DAppRuntime=$Runtime" (Join-Path $projectRoot 'installer/Winisland.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installerPath = Join-Path $installerDirectory "Winisland-Setup-$version-$Runtime.exe"
if (!(Test-Path -LiteralPath $installerPath)) { throw 'Compiled installer not found.' }
$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($installerPath + '.sha256', "$hash  $([IO.Path]::GetFileName($installerPath))`n")
Write-Output "Installer: $installerPath"
if ($Verify) {
    # Verify the exact self-contained payload without installing on the user's
    # computer. Installation/registry scenarios belong in a disposable VM.
    & (Join-Path $PSScriptRoot 'Invoke-Verification.ps1') -ExecutablePath (Join-Path $publishDirectory 'Winisland.exe') -OutputDirectory (Join-Path $projectRoot 'artifacts/installer-verification/payload')
}

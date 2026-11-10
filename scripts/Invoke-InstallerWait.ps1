param(
    [Parameter(Mandatory = $true)][string]$JobPath,
    [switch]$AllowDesktopChanges
)
$ErrorActionPreference = 'Stop'
if (!$AllowDesktopChanges) { throw 'Installer process waiting is internal to disposable VM verification and requires -AllowDesktopChanges.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$verificationRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/installer-verification')) + [IO.Path]::DirectorySeparatorChar
$JobPath = [IO.Path]::GetFullPath($JobPath)
if (!$JobPath.StartsWith($verificationRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer wait job is outside the owned VM fixtures.' }
$job = Get-Content -LiteralPath $JobPath -Raw | ConvertFrom-Json
$fixture = [IO.Path]::GetFullPath($job.fixtureDirectory)
if (!(Split-Path -Leaf $fixture).StartsWith('vm-') -or !$fixture.StartsWith($verificationRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Invalid disposable VM fixture directory.'
}
$fixturePrefix = $fixture + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($JobPath, $job.executable, $job.stdout, $job.stderr)) {
    if (![IO.Path]::GetFullPath($path).StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer wait paths must remain in their owned fixture.' }
}
if ([IO.Path]::GetFileName($job.executable) -ne 'unins000.exe' -or $job.arguments -notmatch '/VERYSILENT' -or $job.arguments -notmatch '/SUPPRESSMSGBOXES') {
    throw 'This helper waits only for silent fixture uninstallation.'
}
# Inno's original uninstaller can exit before its temporary process finishes
# post-uninstall cleanup. -Wait includes descendants; the parent verifier bounds
# this hidden helper and its complete process tree to 180 seconds.
$process = Start-Process -FilePath $job.executable -ArgumentList $job.arguments -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $job.stdout -RedirectStandardError $job.stderr
exit $process.ExitCode

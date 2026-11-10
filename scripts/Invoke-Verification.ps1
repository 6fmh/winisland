param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$OutputDirectory,
    [ValidateSet('All', 'ControlCenter', 'Spotify', 'Startup', 'Updates')][string]$Suite = 'All'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/verification' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$reportName = if ($Suite -eq 'ControlCenter') { 'control-center-results.json' } else { 'results.json' }
$resultPath = Join-Path $OutputDirectory $reportName
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
$entrypoint = if ($Suite -eq 'ControlCenter') { '--control-center-test' } elseif ($Suite -eq 'Spotify') { '--self-test --spotify-only' } elseif ($Suite -eq 'Startup') { '--startup-test' } elseif ($Suite -eq 'Updates') { '--self-test --updates-only' } else { '--self-test' }
# Windows command-line parsing treats a backslash before a closing quote as an
# escape. Preserve trailing separators, including drive/UNC roots.
$quotedOutput = '"' + ($OutputDirectory -replace '(\\+)$', '$1$1') + '"'
$arguments = '{0} --output {1}' -f $entrypoint, $quotedOutput
$previousGuiErrors = $env:DOTNET_DISABLE_GUI_ERRORS
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $ExecutablePath
$start.Arguments = $arguments
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
try {
    # Apphost can show a missing-runtime dialog before our entry point runs.
    $env:DOTNET_DISABLE_GUI_ERRORS = '1'
    if (!$process.Start()) { throw 'Could not start verification.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
} catch {
    $process.Dispose()
    throw
} finally { $env:DOTNET_DISABLE_GUI_ERRORS = $previousGuiErrors }
try {
    if (!$process.WaitForExit(200000)) {
        # The launcher job contains the worker and all its child fixtures.
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
        throw "Background verification timed out. See $OutputDirectory"
    }
    if ($process.ExitCode -ne 0) { throw "Verification failed (exit code $($process.ExitCode), 0x$($process.ExitCode.ToString('X8'))). See the reports in $OutputDirectory" }
    if (!(Test-Path -LiteralPath $resultPath)) { throw 'Verification exited without a result report.' }
    $report = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (!$report.passed) { throw "Verification reported failure. See $resultPath" }
    Write-Output "Background verification passed. See $resultPath"
} finally {
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'launcher-stdout.log'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'launcher-stderr.log'), $stderr.GetAwaiter().GetResult())
    $process.Dispose()
}

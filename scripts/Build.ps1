param(
    [switch]$SelfContained,
    [switch]$Verify,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ($Verify) {
    $hostRuntime = 'win-' + [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($Runtime -ne $hostRuntime) { throw "Verification requires the matching Windows architecture ($hostRuntime)." }
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $localDotnet) {
    $buildTool = $localDotnet
} elseif ($dotnetCommand -and (& $dotnetCommand.Source --list-sdks)) {
    $buildTool = $dotnetCommand.Source
} else {
    $toolsDirectory = Join-Path $projectRoot '.tools'
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    $installerPath = Join-Path $toolsDirectory 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath
    & $installerPath -Channel 8.0 -InstallDir (Join-Path $toolsDirectory 'dotnet') -NoPath
    $buildTool = $localDotnet
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$publishDirectory = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } elseif ($Verify) { Join-Path $projectRoot "artifacts/verification/candidate/$Runtime" } else { Join-Path $projectRoot "dist/$Runtime" }
# Single-file self-contained builds use composite ReadyToRun dependencies;
# portable builds use standalone images. Sharing the SDK's R2R cache can leave
# portable dependencies pointing at an absent self-contained composite image.
$publishFlavor = if ($SelfContained) { 'self-contained' } else { 'portable' }
$intermediateDirectory = (Join-Path $projectRoot "src/Winisland/obj/publish/$publishFlavor/$Runtime/")
$buildDirectory = (Join-Path $projectRoot "src/Winisland/bin/publish/$publishFlavor/$Runtime/")
$publishArguments = @('publish', (Join-Path $projectRoot 'src/Winisland/Winisland.csproj'),
    '-c', 'Release', '-r', $Runtime, '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(),
    '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=false', '-p:PublishReadyToRun=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:IntermediateOutputPath=$intermediateDirectory", "-p:OutputPath=$buildDirectory",
    '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $publishDirectory, '--nologo')
# Portable images target the minimum supported .NET 8 references. Self-contained
# installers use the SDK's current patched runtime.
if (!$SelfContained) { $publishArguments += '-p:TargetLatestRuntimePatch=false' }
& $buildTool @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$executable = Join-Path $publishDirectory 'Winisland.exe'
Write-Output "Built: $executable"
if ($Verify) {
    & (Join-Path $PSScriptRoot 'Invoke-Verification.ps1') -ExecutablePath $executable
}

param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [switch]$AllowDesktopChanges
)
$ErrorActionPreference = 'Stop'
if (!$AllowDesktopChanges) {
    throw 'This check installs, upgrades, starts and uninstalls Winisland and changes sign-in/Drop tray settings. Run only in a disposable Windows VM with -AllowDesktopChanges. Use Build-Installer.ps1 -Verify on a personal computer.'
}
if ([Environment]::OSVersion.Version.Build -lt 22000) { throw 'Installer verification requires a Windows 11/Server 2025 disposable runner.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $projectRoot ('artifacts/installer-verification/vm-' + [guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$previousDataDirectory = $env:WINISLAND_DATA_DIR
$env:WINISLAND_DATA_DIR = Join-Path $outputDirectory 'profile'
New-Item -ItemType Directory -Path $env:WINISLAND_DATA_DIR -Force | Out-Null
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4966D65C-F470-49A9-B206-6D4A9C710BDA}_is1'
$backupKey = 'Software\Winisland\Installer'
$dropTrayKey = 'Software\Microsoft\Windows\CurrentVersion\CDP'
$startupKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$registry = [Microsoft.Win32.Registry]::CurrentUser
foreach ($path in @($uninstallKey, $backupKey)) {
    $existing = $registry.OpenSubKey($path)
    if ($existing) { $existing.Dispose(); throw "Refusing to overwrite an existing installation or backup: HKCU\$path" }
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $taskName = 'Winisland-' + $identity.User.Value } finally { $identity.Dispose() }
$scheduler = New-Object -ComObject Schedule.Service
$scheduler.Connect()
$taskFolder = $scheduler.GetFolder('\')
function Get-SigninTask {
    try { return $taskFolder.GetTask($taskName) }
    catch {
        if ($_.Exception.HResult -eq -2147024894 -or $_.Exception.InnerException.HResult -eq -2147024894) { return $null }
        throw
    }
}
if (Get-SigninTask) { throw 'Refusing to overwrite an existing Winisland sign-in task.' }
$tray = $registry.CreateSubKey($dropTrayKey)
$originalTrayExists = $tray.GetValueNames() -contains 'DragTrayEnabled'
$originalTrayValue = $tray.GetValue('DragTrayEnabled')
$originalTrayKind = if ($originalTrayExists) { $tray.GetValueKind('DragTrayEnabled') } else { $null }
$startup = $registry.CreateSubKey($startupKey)
$originalStartupExists = $startup.GetValueNames() -contains 'Winisland'
$originalStartupValue = $startup.GetValue('Winisland')
$originalStartupKind = if ($originalStartupExists) { $startup.GetValueKind('Winisland') } else { $null }
$checks = [Collections.Generic.List[object]]::new()
$activeInstallDirectory = $null
$ownedRestartPid = $null
$passed = $false
$verificationFailure = $null

function Assert-Installer([bool]$Condition, [string]$Name) {
    $checks.Add([pscustomobject]@{ check = $Name; passed = $Condition })
    if (!$Condition) { throw "Installer verification failed: $Name" }
    Write-Output "PASS: $Name"
}
function Invoke-InstallerProcess([string]$Executable, [string]$Arguments, [switch]$FileBackedResult) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    # A restarted app inherits console pipes. The update helper already writes
    # update-result.json and profile logs; waiting for its console EOF would
    # wait for the app that the verifier must subsequently inspect and stop.
    $start.RedirectStandardOutput = !$FileBackedResult
    $start.RedirectStandardError = !$FileBackedResult
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (!$process.Start()) { throw 'Could not start installer process.' }
        if (!$FileBackedResult) {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
        }
        if (!$process.WaitForExit(180000)) {
            # On current PowerShell, include this test's owned descendants.
            if ($process.GetType().GetMethod('Kill', [type[]]@([bool]))) { $process.Kill($true) }
            else {
                & "$env:SystemRoot/System32/taskkill.exe" /PID $process.Id /T /F *> (Join-Path $outputDirectory 'process-timeout-cleanup.log')
            }
            $process.WaitForExit(5000) | Out-Null
            throw "Installer process timed out. See $outputDirectory"
        }
        if (!$FileBackedResult) {
            $stdout.GetAwaiter().GetResult() | Out-File -LiteralPath (Join-Path $outputDirectory 'process-stdout.log') -Append
            $stderr.GetAwaiter().GetResult() | Out-File -LiteralPath (Join-Path $outputDirectory 'process-stderr.log') -Append
        }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function Invoke-Setup([string]$InstallDirectory, [string]$LogName, [bool]$StartWithWindows = $false, [bool]$Update = $false) {
    $tasks = if ($StartWithWindows) { 'startwithwindows' } else { '' }
    $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /TASKS="{0}" /DIR="{1}" /LOG="{2}"' -f $tasks, $InstallDirectory, (Join-Path $outputDirectory $LogName)
    if ($Update) { $arguments += ' /UPDATE=1' }
    $exitCode = Invoke-InstallerProcess $InstallerPath $arguments
    Assert-Installer ($exitCode -eq 0) "Setup exits successfully ($LogName)"
    Assert-Installer (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Winisland.exe')) 'Installs the app executable'
    Assert-Installer (Test-Path -LiteralPath (Join-Path $InstallDirectory 'browser-extension/manifest.json')) 'Installs the player companion'
    foreach ($license in @('Inter-LICENSE.txt', 'Winisland-LICENSE.txt', 'NAudio-LICENSE.txt', 'DotNet-LICENSE.txt', 'DotNet-THIRD-PARTY-NOTICES.txt')) {
        Assert-Installer (Test-Path -LiteralPath (Join-Path $InstallDirectory "licenses/$license")) "Includes $license"
    }
    Assert-Installer ($tray.GetValue('DragTrayEnabled') -eq 0) 'Installation disables Windows Drop tray'
}
function Invoke-Uninstall([string]$InstallDirectory, [string]$LogName) {
    $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="{0}"' -f (Join-Path $outputDirectory $LogName)
    $waitJob = Join-Path $outputDirectory ($LogName + '-wait.json')
    [IO.File]::WriteAllText($waitJob, (@{
        executable = Join-Path $InstallDirectory 'unins000.exe'; arguments = $arguments; fixtureDirectory = $outputDirectory
        stdout = Join-Path $outputDirectory ($LogName + '-stdout.log'); stderr = Join-Path $outputDirectory ($LogName + '-stderr.log')
    } | ConvertTo-Json))
    $waitHelper = Join-Path $PSScriptRoot 'Invoke-InstallerWait.ps1'
    $vmShell = (Get-Process -Id $PID).Path
    $exitCode = Invoke-InstallerProcess $vmShell ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -JobPath "{1}" -AllowDesktopChanges' -f $waitHelper, $waitJob)
    Assert-Installer ($exitCode -eq 0) "Uninstall exits successfully ($LogName)"
    Assert-Installer (!(Test-Path -LiteralPath (Join-Path $InstallDirectory 'Winisland.exe'))) 'Uninstall removes the app executable'
    Assert-Installer ($null -eq (Get-SigninTask)) 'Uninstall removes the account-specific sign-in task'
    $backup = $registry.OpenSubKey($backupKey)
    $backupExists = $null -ne $backup
    if ($backup) {
        try {
            $values = [ordered]@{}
            foreach ($name in $backup.GetValueNames()) { $values[$name] = $backup.GetValue($name) }
            [IO.File]::WriteAllText((Join-Path $outputDirectory ($LogName + '-remaining-backup.json')), ($values | ConvertTo-Json))
        } finally { $backup.Dispose() }
    }
    Assert-Installer (!$backupExists) 'Uninstall clears its Drop tray backup'
}
function Stop-OwnedRestart {
    if ($null -eq $script:ownedRestartPid) { return }
    $owned = Get-Process -Id $script:ownedRestartPid -ErrorAction SilentlyContinue
    if ($owned) {
        $expected = [IO.Path]::GetFullPath((Join-Path $script:activeInstallDirectory 'Winisland.exe'))
        if ($owned.Path -ne $expected -or !$expected.StartsWith($outputDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to stop a process outside the owned disposable installer fixture.'
        }
        Stop-Process -Id $owned.Id
        $owned.WaitForExit(5000) | Out-Null
    }
    $script:ownedRestartPid = $null
}
function Assert-SigninTask([string]$ApplicationPath) {
    $task = Get-SigninTask
    Assert-Installer ($null -ne $task) 'Creates an account-specific sign-in task'
    $definition = $triggers = $trigger = $taskPrincipal = $taskSettings = $null
    try {
        [IO.File]::WriteAllText((Join-Path $outputDirectory 'signin-task.xml.log'), [string]$task.Xml)
        [xml]$xml = $task.Xml
        # Exported XML omits default values. Read effective Scheduler properties
        # so missing PT0S/LeastPrivilege/false elements are not mistaken for gates.
        $definition = $task.Definition
        $triggers = $definition.Triggers
        $trigger = $triggers.Item(1)
        $taskPrincipal = $definition.Principal
        $taskSettings = $definition.Settings
        $delayText = [string]$trigger.Delay
        $delay = if ([string]::IsNullOrEmpty($delayText)) { [TimeSpan]::Zero } else { [Xml.XmlConvert]::ToTimeSpan($delayText) }
        $triggerIdentity = [string]$trigger.UserId
        $triggerSid = if ($triggerIdentity.StartsWith('S-1-')) { $triggerIdentity } else { ([Security.Principal.NTAccount]::new($triggerIdentity)).Translate([Security.Principal.SecurityIdentifier]).Value }
        $nativeSettings = [ordered]@{
            triggerCount = [int]$triggers.Count; triggerType = [int]$trigger.Type; triggerEnabled = [bool]$trigger.Enabled; triggerUserSid = $triggerSid
            delay = $delayText; delaySeconds = $delay.TotalSeconds; runLevel = [int]$taskPrincipal.RunLevel; logonType = [int]$taskPrincipal.LogonType
            runOnlyIfNetworkAvailable = [bool]$taskSettings.RunOnlyIfNetworkAvailable
            disallowStartIfOnBatteries = [bool]$taskSettings.DisallowStartIfOnBatteries; stopIfGoingOnBatteries = [bool]$taskSettings.StopIfGoingOnBatteries
        }
        [IO.File]::WriteAllText((Join-Path $outputDirectory 'signin-task-native.json'), ($nativeSettings | ConvertTo-Json))
        Assert-Installer ($nativeSettings.triggerCount -eq 1 -and $nativeSettings.triggerType -eq 9 -and $nativeSettings.triggerEnabled -and
            $nativeSettings.triggerUserSid -eq $taskName.Substring('Winisland-'.Length) -and $nativeSettings.delaySeconds -eq 0 -and
            $nativeSettings.runLevel -eq 0 -and $nativeSettings.logonType -eq 3 -and !$nativeSettings.runOnlyIfNetworkAvailable -and
            !$nativeSettings.disallowStartIfOnBatteries -and !$nativeSettings.stopIfGoingOnBatteries) 'Sign-in task runs with no delay, network or battery gate and no elevation'
        Assert-Installer ($xml.Task.Actions.Exec.Command -eq $ApplicationPath -and $xml.Task.Actions.Exec.Arguments -eq '--background') 'Sign-in task targets the installed background app'
        Assert-Installer (!($startup.GetValueNames() -contains 'Winisland')) 'Scheduled startup removes the legacy Run entry'
    } finally {
        foreach ($comObject in @($taskSettings, $taskPrincipal, $trigger, $triggers, $definition, $task)) {
            if ($null -ne $comObject -and [Runtime.InteropServices.Marshal]::IsComObject($comObject)) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject) | Out-Null }
        }
    }
}

try {
    foreach ($scenario in @('enabled', 'absent', 'disabled', 'user-changed')) {
        $activeInstallDirectory = Join-Path $outputDirectory "$scenario/app"
        if ($scenario -eq 'absent') { $tray.DeleteValue('DragTrayEnabled', $false) }
        else { $tray.SetValue('DragTrayEnabled', $(if ($scenario -eq 'disabled') { 0 } else { 1 }), [Microsoft.Win32.RegistryValueKind]::DWord) }
        $startup.SetValue('Winisland', '"C:\Previous portable copy\Winisland.exe" --background')
        Invoke-Setup $activeInstallDirectory "$scenario-install.log" ($scenario -eq 'enabled')
        $application = Join-Path $activeInstallDirectory 'Winisland.exe'
        $registration = $registry.OpenSubKey($uninstallKey)
        try {
            Assert-Installer ($null -ne $registration -and $registration.GetValue('InstallLocation').TrimEnd('\') -eq $activeInstallDirectory.TrimEnd('\')) 'Registers the per-user uninstaller at the requested location'
        } finally { if ($registration) { $registration.Dispose() } }
        if ($scenario -eq 'enabled') {
            Assert-SigninTask $application
            # Exercise the exact uninstall command against mixed ownership.
            # The disposable runner owns all registrations created here.
            $foreignApplication = Join-Path $outputDirectory 'later-portable/Winisland.exe'
            $foreignRun = '"' + $foreignApplication + '" --background'
            $startup.SetValue('Winisland', $foreignRun)
            Assert-Installer ((Invoke-InstallerProcess $application '--configure-startup remove-owned') -eq 0) 'Owned startup removal completes for a mixed task and Run registration'
            $settingsPath = Join-Path $env:WINISLAND_DATA_DIR 'settings.json'
            $removalSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            Assert-Installer ($null -eq (Get-SigninTask) -and $startup.GetValue('Winisland') -eq $foreignRun -and $removalSettings.StartWithWindows) 'Uninstall command removes its own task and preserves a later portable Run registration and preference'
            Assert-Installer ((Invoke-InstallerProcess $application '--configure-startup preserve') -eq 0) 'Restores owned startup after the mixed Run scenario'
            $ownTask = Get-SigninTask
            try { [xml]$foreignTaskXml = [string]$ownTask.Xml }
            finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($ownTask) | Out-Null }
            $foreignTaskXml.Task.Actions.Exec.Command = $foreignApplication
            $createdTask = $taskFolder.RegisterTask($taskName, $foreignTaskXml.OuterXml, 6, $taskName.Substring('Winisland-'.Length), $null, 3, $null)
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($createdTask) | Out-Null
            $startup.SetValue('Winisland', '"' + $application + '" --background')
            Assert-Installer ((Invoke-InstallerProcess $application '--configure-startup remove-owned') -eq 0) 'Owned startup removal completes for a foreign task and owned Run registration'
            $remainingTask = Get-SigninTask
            try {
                [xml]$remainingTaskXml = [string]$remainingTask.Xml
                $removalSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
                Assert-Installer ($remainingTaskXml.Task.Actions.Exec.Command -eq $foreignApplication -and
                    !($startup.GetValueNames() -contains 'Winisland') -and $removalSettings.StartWithWindows) 'Uninstall command preserves a later portable task and preference while removing its own Run entry'
            } finally { if ($remainingTask) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($remainingTask) | Out-Null } }
            Assert-Installer ((Invoke-InstallerProcess $application '--configure-startup preserve') -eq 0) 'Restores owned startup after the foreign task scenario'
            Assert-SigninTask $application
            $settingsPath = Join-Path $env:WINISLAND_DATA_DIR 'settings.json'
            $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            $saved.NotchWidth = 230
            $saved.ReducedMotion = $true
            $saved.HasLaunched = $true
            [IO.File]::WriteAllText($settingsPath, ($saved | ConvertTo-Json))
            $dataPath = Join-Path $env:WINISLAND_DATA_DIR 'user-data-fixture.txt'
            [IO.File]::WriteAllText($dataPath, 'Settings and original data must survive an update.')
            $preservedHash = (Get-FileHash -LiteralPath $dataPath -Algorithm SHA256).Hash
            Invoke-Setup $activeInstallDirectory 'upgrade-install.log' $false $true
            Assert-SigninTask $application
            $after = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            Assert-Installer ($after.StartWithWindows -and $after.NotchWidth -eq 230 -and $after.ReducedMotion) 'An in-app upgrade preserves startup and notch preferences'
            $backup = $registry.OpenSubKey($backupKey)
            try { Assert-Installer ($backup.GetValue('DropTrayOriginalValue') -eq 1) 'An upgrade retains the original Drop tray setting' }
            finally { $backup.Dispose() }

            # Force a failed settings-file commit after task mutation. This VM
            # regression proves the task and exact Run value are rolled back.
            $savedSettingsPath = Join-Path $env:WINISLAND_DATA_DIR 'settings-before-rollback.json'
            Move-Item -LiteralPath $settingsPath -Destination $savedSettingsPath
            New-Item -ItemType Directory -Path $settingsPath | Out-Null
            $startup.SetValue('Winisland', '"C:\Rollback fixture\Winisland.exe" --background')
            try {
                $exitCode = Invoke-InstallerProcess $application '--configure-startup false'
                Assert-Installer ($exitCode -ne 0 -and $null -ne (Get-SigninTask) -and
                    $startup.GetValue('Winisland') -eq '"C:\Rollback fixture\Winisland.exe" --background') 'A failed preference commit restores the task and exact previous Run entry'
            } finally {
                $checkedSettingsPath = [IO.Path]::GetFullPath($settingsPath)
                if (!$checkedSettingsPath.StartsWith($outputDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe rollback fixture path.' }
                Remove-Item -LiteralPath $checkedSettingsPath
                Move-Item -LiteralPath $savedSettingsPath -Destination $settingsPath
            }
            Assert-Installer ((Invoke-InstallerProcess $application '--configure-startup preserve') -eq 0) 'Restores the saved sign-in preference after rollback verification'

            # Exercise the actual bundled helper and setup in this disposable
            # VM. No parent remains; the helper must install and restart the app.
            $version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/Winisland/Winisland.csproj'))).Project.PropertyGroup.Version
            $runtime = 'win-' + [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
            $cache = Join-Path $env:WINISLAND_DATA_DIR ('updates/' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $cache -Force | Out-Null
            $stagedInstaller = Join-Path $cache "Winisland-Setup-$version-$runtime.exe"
            Copy-Item -LiteralPath $InstallerPath -Destination $stagedInstaller
            $helper = Join-Path $cache 'Winisland.Update.exe'
            Copy-Item -LiteralPath $application -Destination $helper
            $planPath = Join-Path $cache 'update-plan.json'
            $plan = @{ Package = @{ InstallerPath = $stagedInstaller; Sha256 = (Get-FileHash -LiteralPath $stagedInstaller -Algorithm SHA256).Hash.ToLowerInvariant(); Version = [string]$version };
                ParentId = [int]::MaxValue; ParentStartedUtcTicks = [DateTime]::UtcNow.Ticks; ApplicationPath = $application; InstallDirectory = $activeInstallDirectory }
            [IO.File]::WriteAllText($planPath, ($plan | ConvertTo-Json -Depth 5))
            $exitCode = Invoke-InstallerProcess $helper ('--apply-update "{0}"' -f $planPath) -FileBackedResult
            $outcomePath = Join-Path $env:WINISLAND_DATA_DIR 'update-result.json'
            $outcome = Get-Content -LiteralPath $outcomePath -Raw | ConvertFrom-Json
            $ownedRestartPid = $outcome.restartedPid
            Assert-Installer ($exitCode -eq 0 -and $outcome.success -and $null -ne $ownedRestartPid) 'Actual update helper verifies, runs setup and records a successful restart'
            Start-Sleep -Milliseconds 800
            $restarted = Get-Process -Id $ownedRestartPid -ErrorAction SilentlyContinue
            Assert-Installer ($null -ne $restarted -and $restarted.Path -eq $application -and !$restarted.HasExited) 'The upgraded app is alive at the expected installation path'
            Assert-SigninTask $application
            $after = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            Assert-Installer ($after.StartWithWindows -and $after.NotchWidth -eq 230 -and $after.ReducedMotion -and
                (Get-FileHash -LiteralPath $dataPath -Algorithm SHA256).Hash -eq $preservedHash) 'Real helper upgrade preserves preferences and original user data'
            Stop-OwnedRestart

            & (Join-Path $PSScriptRoot 'Invoke-Verification.ps1') -ExecutablePath $application -OutputDirectory (Join-Path $outputDirectory 'installed-app')
            Assert-Installer $true 'Installed self-contained app passes its private-desktop suite'
        } else {
            Assert-Installer ($null -eq (Get-SigninTask)) 'Unselected sign-in startup creates no task'
        }
        if ($scenario -eq 'user-changed') { $tray.SetValue('DragTrayEnabled', 1, [Microsoft.Win32.RegistryValueKind]::DWord) }
        Invoke-Uninstall $activeInstallDirectory "$scenario-uninstall.log"
        $activeInstallDirectory = $null
        if ($scenario -eq 'absent') { Assert-Installer (!($tray.GetValueNames() -contains 'DragTrayEnabled')) 'Uninstall restores an originally absent Drop tray setting' }
        else { Assert-Installer ($tray.GetValue('DragTrayEnabled') -eq $(if ($scenario -eq 'disabled') { 0 } else { 1 })) "Uninstall restores or preserves the expected Drop tray setting ($scenario)" }
    }
    $passed = $true
} catch {
    $verificationFailure = $_.Exception.ToString()
    throw
} finally {
    try {
        Stop-OwnedRestart
        if ($activeInstallDirectory -and (Test-Path -LiteralPath (Join-Path $activeInstallDirectory 'unins000.exe'))) {
            try { Invoke-Uninstall $activeInstallDirectory 'cleanup-uninstall.log' }
            catch {
                [IO.File]::WriteAllText((Join-Path $outputDirectory 'cleanup-error.log'), $_.Exception.ToString())
                if (!$verificationFailure) { throw }
            }
        }
    } finally {
        if ($originalTrayExists) { $tray.SetValue('DragTrayEnabled', $originalTrayValue, $originalTrayKind) }
        else { $tray.DeleteValue('DragTrayEnabled', $false) }
        if ($originalStartupExists) { $startup.SetValue('Winisland', $originalStartupValue, $originalStartupKind) }
        else { $startup.DeleteValue('Winisland', $false) }
        $tray.Dispose()
        $startup.Dispose()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskFolder) | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($scheduler) | Out-Null
        $env:WINISLAND_DATA_DIR = $previousDataDirectory
        [IO.File]::WriteAllText((Join-Path $outputDirectory 'results.json'), (@{ passed = $passed; error = $verificationFailure; checks = @($checks.ToArray()); desktopChanges = 'disposable-vm-only'; installerSha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json -Depth 6))
    }
}
Write-Output "Installer verification passed: $outputDirectory"

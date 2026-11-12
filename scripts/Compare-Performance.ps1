param(
    [Parameter(Mandatory = $true)][string[]]$BaselineDirectories,
    [Parameter(Mandatory = $true)][string[]]$OptimizedDirectories,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'

# Read existing isolated Performance reports; this script never launches a UI.
if ($BaselineDirectories.Count -lt 3 -or $BaselineDirectories.Count -ne $OptimizedDirectories.Count) {
    throw 'Supply at least three balanced baseline/optimized runs; exclude calibration runs.'
}
$allDirectories = @(@($BaselineDirectories) + @($OptimizedDirectories) | ForEach-Object { [IO.Path]::GetFullPath($_) } | Select-Object -Unique)
if ($allDirectories.Count -ne $BaselineDirectories.Count + $OptimizedDirectories.Count) {
    throw 'Each comparison run must have its own report directory.'
}
function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { throw 'Missing measurement samples.' }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}
function Read-Measurements([string]$Directory) {
    $reportPath = Join-Path $Directory 'results.json'
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (!$report.passed -or !$report.privateBuild -or $report.desktop -notlike 'Winisland.Verification.*' -or
        @($report.checks | Where-Object { !$_.passed }).Count -gt 0) {
        throw "Failed or non-private performance report: $reportPath"
    }
    $idle = Get-Content -LiteralPath (Join-Path $Directory 'idle/results.json') -Raw | ConvertFrom-Json
    if (!$idle.passed -or $idle.desktop -ne $report.desktop) { throw 'Idle report failed or used a different desktop.' }
    $frames = $report.workloads[0]
    $upload = $report.workloads[1]
    $nativeHost = $report.workloads[2]
    if ($frames.frameCount -ne 6000 -or $frames.repetitions -ne 5 -or $frames.sampleRate -ne 48000 -or
        $upload.fileBytes -ne 25165824 -or $upload.batches -ne 3 -or $upload.transfersPerBatch -ne 2 -or
        @($nativeHost.samples).Count -ne 5 -or $nativeHost.bridge.packetsPerSample -ne 4096) {
        throw "Unexpected workload shape in $reportPath"
    }
    $metrics = [ordered]@{}
    $naturalIdle = @($idle.samples | Where-Object { $_.Phase -eq 'idle' })
    foreach ($property in @('PrivateBytes', 'WorkingSetBytes', 'ManagedBytes', 'Handles')) {
        $metrics["idle.$property"] = Get-Median @($naturalIdle | ForEach-Object { [double]$_.$property })
    }
    $metrics['idle.allocatedBytesPerSecond'] = [double]$idle.idleAllocatedBytesPerSecond
    $metrics['idle.cpuMsPer5Seconds'] = [double]$idle.idleCpuMs
    foreach ($workload in @('notch-macbook-timeline', 'notch-island-timeline', 'dynamic-player-timeline', 'spectrum-two-second-stereo-pcm')) {
        $samples = @($frames.measurements | Where-Object { $_.Workload -eq $workload })
        if ($samples.Count -ne 5) { throw "Missing frame workload $workload" }
        foreach ($property in @('WallMs', 'CpuMs', 'ThreadAllocatedBytes')) {
            $metrics["$workload.$property"] = Get-Median @($samples | ForEach-Object { [double]$_.$property })
        }
    }
    foreach ($property in @('WallMs', 'CpuMs', 'AllocatedBytes', 'Changed', 'PropertyChanged', 'BindingReads')) {
        $metrics["upload.$property"] = Get-Median @($upload.samples | ForEach-Object { [double]$_.$property })
    }
    foreach ($property in @('PrivateBytes', 'WorkingSetBytes', 'Handles', 'CpuMs')) {
        $metrics["nativeHost.handshake.$property"] = Get-Median @($nativeHost.samples | ForEach-Object { [double]$_.handshake.$property })
    }
    $metrics['nativeHost.handshakeMs'] = Get-Median @($nativeHost.samples | ForEach-Object { [double]$_.handshakeMs })
    $metrics['nativeHost.idleCpuMsPerSecond'] = Get-Median @($nativeHost.samples | ForEach-Object { [double]$_.idleCpuMs })
    foreach ($property in @('notifications', 'elapsedMs', 'allocatedBytes', 'cpuMs')) {
        $metrics["bridge.$property"] = Get-Median @($nativeHost.bridge.samples | ForEach-Object { [double]$_.$property })
    }
    return [pscustomobject]@{
        directory = [IO.Path]::GetFullPath($Directory)
        reportSha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash
        desktop = $report.desktop
        metrics = $metrics
    }
}
$baseline = @($BaselineDirectories | ForEach-Object { Read-Measurements $_ })
$optimized = @($OptimizedDirectories | ForEach-Object { Read-Measurements $_ })
$comparisons = @(foreach ($key in $baseline[0].metrics.Keys) {
    $before = @($baseline | ForEach-Object { [double]$_.metrics[$key] })
    $after = @($optimized | ForEach-Object { [double]$_.metrics[$key] })
    $beforeMedian = Get-Median $before
    $afterMedian = Get-Median $after
    [pscustomobject]@{
        metric = $key
        baselineMedian = $beforeMedian
        optimizedMedian = $afterMedian
        reductionPercent = if ($beforeMedian -ne 0) { 100 * (1 - $afterMedian / $beforeMedian) } else { $null }
        speedup = if ($afterMedian -gt 0) { $beforeMedian / $afterMedian } else { $null }
        baselineMin = ($before | Measure-Object -Minimum).Minimum
        baselineMax = ($before | Measure-Object -Maximum).Maximum
        optimizedMin = ($after | Measure-Object -Minimum).Minimum
        optimizedMax = ($after | Measure-Object -Maximum).Maximum
        baselineRunMedians = $before
        optimizedRunMedians = $after
    }
})
$result = [pscustomobject]@{
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    note = 'Median of independent run medians, with run range and raw reports. Method timings are not FPS. CPU is process-wide and quantized; working set varies with pressure. Forced-GC diagnostics excluded.'
    baselineRuns = $baseline
    optimizedRuns = $optimized
    comparisons = $comparisons
}
$absoluteOutput = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $absoluteOutput) -Force | Out-Null
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $absoluteOutput
$comparisons | Select-Object metric, baselineMedian, optimizedMedian, reductionPercent | Format-Table -AutoSize

<#
.SYNOPSIS
    Runs the Dudu.WindowsHarness "performance" scenario against a published
    Dudu.App.exe and fails (exit 1) if any task-23 performance gate trips.

.DESCRIPTION
    Launches `dotnet run --project tests/Dudu.WindowsHarness -c Release --
    --scenario performance --executable <Executable> --output <Output>`,
    which itself writes a JSON report and evaluates the same thresholds in
    C# (tests/Dudu.WindowsHarness/PerformanceScenario.cs,
    PerformanceThresholds.Evaluate). This script re-reads that JSON and
    applies Test-PerformanceThresholds -- a second, independent
    implementation of the identical reaches(>=)/exceeds(>) rule -- as its own
    gate, so a regression in either implementation is caught by the other.

    Test-PerformanceThresholds is a pure function (metrics in, failure
    strings out) with no side effects, dot-sourceable without running the
    scenario or touching a real executable. This is what
    tests/scripts/run-performance-gates.tests.ps1 loads and exercises on a
    non-Windows build host, per task 23's accepted-evidence rules.

.PARAMETER Executable
    Path to a published Dudu.App.exe. Required unless this file is only
    being dot-sourced for its function.

.PARAMETER Output
    Where the harness should write its performance-report.json. Defaults to
    artifacts/performance/performance-report.json (matching
    PerformanceScenario.cs's own default), resolved against the repo root.

.PARAMETER IdleSeconds
    Seconds to sample CPU while the pet is idle before starting the
    animation sampling window. Forwarded to the harness as --idle-seconds.
    Defaults to the harness's own default (five minutes) when omitted.

.PARAMETER AnimationSeconds
    Seconds to sample CPU while the pet's idle animation plays. Forwarded to
    the harness as --animation-seconds. Defaults to the harness's own
    default (three minutes) when omitted.
#>
[CmdletBinding()]
param(
    [string]$Executable,
    [string]$Output,
    [int]$IdleSeconds,
    [int]$AnimationSeconds
)

# Explicit rather than relying on command auto-loading: some minimal/vendored
# pwsh builds (used to run this file's tests on a non-Windows build host)
# don't reliably auto-load Utility cmdlets such as Write-Host on first use.
Import-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue
Import-Module Microsoft.PowerShell.Management -ErrorAction SilentlyContinue

# Reaches/exceeds thresholds per the task-23 controller ruling, mirroring
# PerformanceThresholds in tests/Dudu.WindowsHarness/PerformanceScenario.cs
# exactly: startup time, CPU, and working set fail once they *reach* the
# limit (>=); the allocation slope fails only once it *exceeds* the limit
# (>). Kept as script-scope constants (not buried in the function) so both
# this script and its tests reference the same numbers.
$script:StartupMillisecondsLimit = 3000.0
$script:IdleCpuPercentLimit = 1.0
$script:AnimationCpuPercentLimit = 3.0
$script:PeakWorkingSetBytesLimit = 200.0 * 1024 * 1024
$script:AllocationSlopeMegabytesPerHourLimit = 1.0

function Test-PerformanceThresholds {
    <#
    .SYNOPSIS
        Pure function: given a performance-report hashtable (or any object
        with the same property names PerformanceReport.ToJson() writes),
        returns an array of human-readable failure strings. An empty array
        means every gate passed.
    .PARAMETER Report
        A hashtable/PSCustomObject with numeric members:
        LaunchToFirstOverlayMilliseconds, IdleCpuPercent,
        AnimationCpuPercent, PeakWorkingSetBytes,
        AllocationSlopeMegabytesPerHour (or the camelCase JSON property
        names the harness writes -- both are accepted, see ConvertTo-Report
        below for the JSON mapping actually used by the main script path).
    #>
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Report
    )

    $failures = @()

    if ($Report.LaunchToFirstOverlayMilliseconds -ge $script:StartupMillisecondsLimit) {
        $failures += "startup time reaches $($script:StartupMillisecondsLimit.ToString('F0')) ms (observed $($Report.LaunchToFirstOverlayMilliseconds.ToString('F0')) ms)"
    }

    if ($Report.IdleCpuPercent -ge $script:IdleCpuPercentLimit) {
        $failures += "idle CPU reaches $($script:IdleCpuPercentLimit.ToString('F1'))% (observed $($Report.IdleCpuPercent.ToString('F2'))%)"
    }

    if ($Report.AnimationCpuPercent -ge $script:AnimationCpuPercentLimit) {
        $failures += "animation CPU reaches $($script:AnimationCpuPercentLimit.ToString('F1'))% (observed $($Report.AnimationCpuPercent.ToString('F2'))%)"
    }

    if ($Report.PeakWorkingSetBytes -ge $script:PeakWorkingSetBytesLimit) {
        $limitMb = $script:PeakWorkingSetBytesLimit / (1024 * 1024)
        $observedMb = $Report.PeakWorkingSetBytes / (1024 * 1024)
        $failures += "peak working set reaches $($limitMb.ToString('F0')) MB (observed $($observedMb.ToString('F1')) MB)"
    }

    if ($Report.AllocationSlopeMegabytesPerHour -gt $script:AllocationSlopeMegabytesPerHourLimit) {
        $failures += "allocation slope exceeds $($script:AllocationSlopeMegabytesPerHourLimit.ToString('F1')) MB/hour (observed $($Report.AllocationSlopeMegabytesPerHour.ToString('F2')) MB/hour)"
    }

    return $failures
}

function ConvertTo-PerformanceReportHashtable {
    <#
    .SYNOPSIS
        Maps the camelCase JSON property names PerformanceReport.ToJson()
        writes onto the property names Test-PerformanceThresholds expects.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [PSCustomObject]$Json
    )

    return @{
        LaunchToFirstOverlayMilliseconds = [double]$Json.launchToFirstOverlayMs
        IdleCpuPercent                   = [double]$Json.idleCpuPercent
        AnimationCpuPercent              = [double]$Json.animationCpuPercent
        PeakWorkingSetBytes              = [double]$Json.peakWorkingSetBytes
        AllocationSlopeMegabytesPerHour  = [double]$Json.allocationSlopeMbPerHour
    }
}

# Dot-source guard: tests/scripts/run-performance-gates.tests.ps1 dot-sources
# this file to load Test-PerformanceThresholds without running the harness
# or requiring -Executable. $MyInvocation.InvocationName is '.' only when
# dot-sourced; when run directly (or via `pwsh scripts/run-performance-gates.ps1 ...`)
# it is the script's own path/name, so the launcher logic below only runs
# for a direct invocation.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Executable)) {
    throw "-Executable <path-to-Dudu.App.exe> is required."
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$harnessProject = Join-Path $repoRoot "tests/Dudu.WindowsHarness"

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repoRoot "artifacts/performance/performance-report.json"
}

$harnessArgs = @(
    "run", "--project", $harnessProject, "-c", "Release", "--",
    "--scenario", "performance",
    "--executable", $Executable,
    "--output", $Output
)
if ($IdleSeconds -gt 0) {
    $harnessArgs += @("--idle-seconds", $IdleSeconds)
}
if ($AnimationSeconds -gt 0) {
    $harnessArgs += @("--animation-seconds", $AnimationSeconds)
}

Write-Host "Running: dotnet $($harnessArgs -join ' ')"
& dotnet @harnessArgs
$harnessExitCode = $LASTEXITCODE

if (-not (Test-Path $Output)) {
    throw "The harness did not write a performance report to '$Output' (exit code $harnessExitCode)."
}

$reportJson = Get-Content -Raw -Path $Output | ConvertFrom-Json
$report = ConvertTo-PerformanceReportHashtable -Json $reportJson
$failures = Test-PerformanceThresholds -Report $report

if ($failures.Count -eq 0) {
    Write-Host "All performance gates passed ($Output)."
    exit 0
}

foreach ($failure in $failures) {
    Write-Error "GATE FAILED: $failure"
}

exit 1

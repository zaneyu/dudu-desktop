<#
.SYNOPSIS
    Unit-tests Test-PerformanceThresholds from scripts/run-performance-gates.ps1
    against synthetic metrics, with no dependency on Pester, dotnet, or a real
    Dudu.App.exe -- runnable on any host that has pwsh (including this
    macOS build host, per task 23's accepted-evidence rules).

.DESCRIPTION
    Dot-sources scripts/run-performance-gates.ps1 (its launcher logic is
    gated off for a dot-sourced invocation, see the guard near the bottom of
    that file) to load the pure Test-PerformanceThresholds function, then
    asserts it trips exactly the expected gate -- and none of the others --
    for one case per threshold, covering both the reaches(>=) rule
    (startup/CPU/working-set) and the exceeds(>) rule (allocation slope),
    per the task-23 controller ruling. Exits 0 if every assertion passes,
    1 otherwise, printing a PASS/FAIL line per case either way.
#>
$ErrorActionPreference = "Stop"

# See the matching comment in scripts/run-performance-gates.ps1: explicit
# rather than relying on command auto-loading, which some minimal/vendored
# pwsh builds don't reliably perform on first use.
Import-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue
Import-Module Microsoft.PowerShell.Management -ErrorAction SilentlyContinue

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$scriptUnderTest = Join-Path $repoRoot "scripts/run-performance-gates.ps1"
. $scriptUnderTest

function New-PassingReport {
    return @{
        LaunchToFirstOverlayMilliseconds = 1500.0
        IdleCpuPercent                   = 0.4
        AnimationCpuPercent              = 1.2
        PeakWorkingSetBytes              = 90.0 * 1024 * 1024
        AllocationSlopeMegabytesPerHour  = 0.3
    }
}

$script:FailureCount = 0
$script:CaseCount = 0

function Assert-FailureCount {
    param(
        [string]$Name,
        [string[]]$Failures,
        [int]$ExpectedCount
    )

    $script:CaseCount++
    if ($Failures.Count -eq $ExpectedCount) {
        Write-Host "PASS: $Name (failure count $($Failures.Count))"
    }
    else {
        Write-Host "FAIL: $Name (expected $ExpectedCount failures, got $($Failures.Count): $($Failures -join '; '))"
        $script:FailureCount++
    }
}

function Assert-ContainsFailureMatching {
    param(
        [string]$Name,
        [string[]]$Failures,
        [string]$Pattern
    )

    $script:CaseCount++
    if (($Failures | Where-Object { $_ -match $Pattern }).Count -gt 0) {
        Write-Host "PASS: $Name (found a failure matching '$Pattern')"
    }
    else {
        Write-Host "FAIL: $Name (no failure matched '$Pattern'; got: $($Failures -join '; '))"
        $script:FailureCount++
    }
}

# --- A clean run trips nothing. ---
$clean = New-PassingReport
Assert-FailureCount "a report under every threshold passes cleanly" (Test-PerformanceThresholds -Report $clean) 0

# --- Startup: reaches(>=) the limit. Exactly at the limit must already fail. ---
$atStartupLimit = New-PassingReport
$atStartupLimit.LaunchToFirstOverlayMilliseconds = $script:StartupMillisecondsLimit
Assert-ContainsFailureMatching "startup time AT the limit fails (reaches, >=)" (Test-PerformanceThresholds -Report $atStartupLimit) "startup time reaches"

$belowStartupLimit = New-PassingReport
$belowStartupLimit.LaunchToFirstOverlayMilliseconds = $script:StartupMillisecondsLimit - 1
Assert-FailureCount "startup time just below the limit passes" (Test-PerformanceThresholds -Report $belowStartupLimit) 0

# --- Idle CPU: reaches(>=) the limit. ---
$atIdleCpuLimit = New-PassingReport
$atIdleCpuLimit.IdleCpuPercent = $script:IdleCpuPercentLimit
Assert-ContainsFailureMatching "idle CPU AT the limit fails (reaches, >=)" (Test-PerformanceThresholds -Report $atIdleCpuLimit) "idle CPU reaches"

# --- Animation CPU: reaches(>=) the limit. ---
$atAnimationCpuLimit = New-PassingReport
$atAnimationCpuLimit.AnimationCpuPercent = $script:AnimationCpuPercentLimit
Assert-ContainsFailureMatching "animation CPU AT the limit fails (reaches, >=)" (Test-PerformanceThresholds -Report $atAnimationCpuLimit) "animation CPU reaches"

# --- Peak working set: reaches(>=) the limit. ---
$atWorkingSetLimit = New-PassingReport
$atWorkingSetLimit.PeakWorkingSetBytes = $script:PeakWorkingSetBytesLimit
Assert-ContainsFailureMatching "peak working set AT the limit fails (reaches, >=)" (Test-PerformanceThresholds -Report $atWorkingSetLimit) "peak working set reaches"

# --- Allocation slope: exceeds(>) the limit -- AT the limit must still pass. ---
$atAllocationLimit = New-PassingReport
$atAllocationLimit.AllocationSlopeMegabytesPerHour = $script:AllocationSlopeMegabytesPerHourLimit
Assert-FailureCount "allocation slope AT the limit passes (exceeds, >, not >=)" (Test-PerformanceThresholds -Report $atAllocationLimit) 0

$aboveAllocationLimit = New-PassingReport
$aboveAllocationLimit.AllocationSlopeMegabytesPerHour = $script:AllocationSlopeMegabytesPerHourLimit + 0.01
Assert-ContainsFailureMatching "allocation slope just ABOVE the limit fails (exceeds, >)" (Test-PerformanceThresholds -Report $aboveAllocationLimit) "allocation slope exceeds"

# --- Report integrity: a passing-looking JSON file is not enough. ---
$runId = "11111111-1111-1111-1111-111111111111"
$startedUtc = [DateTimeOffset]::UtcNow.AddSeconds(-1)
$validReport = [PSCustomObject]@{
    schemaVersion = 1
    runId = $runId
    generatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    durationSeconds = 12.5
    harnessCompleted = $true
    launchToFirstOverlayMs = 1500.0
    idleCpuPercent = 0.4
    animationCpuPercent = 1.2
    peakWorkingSetBytes = 90.0 * 1024 * 1024
    allocationSlopeMbPerHour = 0.3
}
Assert-FailureCount "a fresh completed report with a nonzero duration passes integrity checks" `
    (Test-PerformanceReportShape -Json $validReport -ExpectedRunId $runId -StartedUtc $startedUtc) 0

$staleReport = $validReport | Select-Object *
$staleReport.generatedUtc = [DateTimeOffset]::UtcNow.AddMinutes(-10).ToString("O")
Assert-ContainsFailureMatching "a stale report is rejected" `
    (Test-PerformanceReportShape -Json $staleReport -ExpectedRunId $runId -StartedUtc $startedUtc) "stale"

$zeroDurationReport = $validReport | Select-Object *
$zeroDurationReport.durationSeconds = 0
Assert-ContainsFailureMatching "a zero-duration report is rejected" `
    (Test-PerformanceReportShape -Json $zeroDurationReport -ExpectedRunId $runId -StartedUtc $startedUtc) "durationSeconds"

$incompleteReport = $validReport | Select-Object *
$incompleteReport.harnessCompleted = $false
Assert-ContainsFailureMatching "a report without harness success is rejected" `
    (Test-PerformanceReportShape -Json $incompleteReport -ExpectedRunId $runId -StartedUtc $startedUtc) "harness completion"

$malformedReport = $validReport | Select-Object *
$malformedReport.animationCpuPercent = "not-a-number"
Assert-ContainsFailureMatching "a malformed numeric field is rejected" `
    (Test-PerformanceReportShape -Json $malformedReport -ExpectedRunId $runId -StartedUtc $startedUtc) "animationCpuPercent"

# --- Every gate failing at once reports every gate, not just the first. ---
$allFailing = @{
    LaunchToFirstOverlayMilliseconds = $script:StartupMillisecondsLimit + 1
    IdleCpuPercent                   = $script:IdleCpuPercentLimit + 1
    AnimationCpuPercent              = $script:AnimationCpuPercentLimit + 1
    PeakWorkingSetBytes              = $script:PeakWorkingSetBytesLimit + 1
    AllocationSlopeMegabytesPerHour  = $script:AllocationSlopeMegabytesPerHourLimit + 1
}
Assert-FailureCount "every gate failing at once reports all five failures" (Test-PerformanceThresholds -Report $allFailing) 5

# --- ConvertTo-PerformanceReportHashtable maps the harness's camelCase JSON correctly. ---
$json = [PSCustomObject]@{
    launchToFirstOverlayMs   = 1500.0
    idleCpuPercent           = 0.4
    animationCpuPercent      = 1.2
    peakWorkingSetBytes      = 90.0 * 1024 * 1024
    allocationSlopeMbPerHour = 0.3
}
$mapped = ConvertTo-PerformanceReportHashtable -Json $json
$script:CaseCount++
if ($mapped.LaunchToFirstOverlayMilliseconds -eq 1500.0 -and $mapped.AllocationSlopeMegabytesPerHour -eq 0.3) {
    Write-Host "PASS: ConvertTo-PerformanceReportHashtable maps harness JSON field names"
}
else {
    Write-Host "FAIL: ConvertTo-PerformanceReportHashtable mapping produced unexpected values"
    $script:FailureCount++
}

Write-Host ""
Write-Host "$($script:CaseCount - $script:FailureCount)/$($script:CaseCount) cases passed."

if ($script:FailureCount -gt 0) {
    exit 1
}

exit 0

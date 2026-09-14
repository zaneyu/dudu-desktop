<#
.SYNOPSIS
    Unit-tests Assert-NoTrackedGeneratedArtifacts from scripts/verify.ps1,
    including against this repository's real git index -- runnable on any
    host that has pwsh and git, including the macOS build host.

.DESCRIPTION
    Dot-sources scripts/verify.ps1 (its release-verification body is gated
    off for a dot-sourced invocation, see the guard near the bottom of that
    file) to load the pure Assert-* functions, then:

      1. feeds synthetic "tracked" lists and asserts the gate throws for a
         tracked packages.lock.json, for a tracked path under work/, and for
         a tracked path under outputs/;
      2. asserts it stays silent when both lists are empty;
      3. runs it with no arguments, so it reads the real git index, and
         asserts this repository genuinely has no tracked generated file.

    Case 3 is the regression guard for review issue I4: it fails on any
    commit where work/generated-package-locks/**/packages.lock.json is
    tracked. Exits 0 if every assertion passes, 1 otherwise, printing a
    PASS/FAIL line per case either way.
#>
$ErrorActionPreference = "Stop"

# See the matching comment in scripts/run-performance-gates.ps1: explicit
# rather than relying on command auto-loading, which some minimal/vendored
# pwsh builds don't reliably perform on first use.
Import-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue
Import-Module Microsoft.PowerShell.Management -ErrorAction SilentlyContinue

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$scriptUnderTest = Join-Path $repoRoot "scripts/verify.ps1"
. $scriptUnderTest

$script:FailureCount = 0
$script:CaseCount = 0

function Assert-Throws {
    param(
        [string]$Name,
        [scriptblock]$Action,
        [string]$Pattern
    )

    $script:CaseCount++
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -match $Pattern) {
            Write-Host "PASS: $Name (threw, matching '$Pattern')"
        }
        else {
            Write-Host "FAIL: $Name (threw, but message did not match '$Pattern': $($_.Exception.Message))"
            $script:FailureCount++
        }
        return
    }

    Write-Host "FAIL: $Name (did not throw)"
    $script:FailureCount++
}

function Assert-DoesNotThrow {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    $script:CaseCount++
    try {
        & $Action
        Write-Host "PASS: $Name (did not throw)"
    }
    catch {
        Write-Host "FAIL: $Name (threw: $($_.Exception.Message))"
        $script:FailureCount++
    }
}

Assert-Throws "a tracked packages.lock.json fails the gate" {
    Assert-NoTrackedGeneratedArtifacts `
        -TrackedLockFiles @("src/Dudu.Core/packages.lock.json") `
        -TrackedScratchPaths @()
} "packages\.lock\.json"

Assert-Throws "a tracked path under work/ fails the gate" {
    Assert-NoTrackedGeneratedArtifacts `
        -TrackedLockFiles @() `
        -TrackedScratchPaths @("work/generated-package-locks/src-Dudu.Core/packages.lock.json")
} "work/generated-package-locks"

Assert-Throws "a tracked path under outputs/ fails the gate" {
    Assert-NoTrackedGeneratedArtifacts `
        -TrackedLockFiles @() `
        -TrackedScratchPaths @("outputs/report.json")
} "outputs/report\.json"

Assert-DoesNotThrow "nothing tracked passes the gate" {
    Assert-NoTrackedGeneratedArtifacts -TrackedLockFiles @() -TrackedScratchPaths @()
}

Assert-DoesNotThrow "this repository has no tracked generated file (real git index)" {
    Push-Location $repoRoot
    try {
        Assert-NoTrackedGeneratedArtifacts
    }
    finally {
        Pop-Location
    }
}

Write-Host ""
if ($script:FailureCount -eq 0) {
    Write-Host "verify.tests.ps1: PASS ($script:CaseCount cases)"
    exit 0
}

Write-Host "verify.tests.ps1: FAIL ($script:FailureCount of $script:CaseCount cases failed)"
exit 1

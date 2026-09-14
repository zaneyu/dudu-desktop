#Requires -Version 7.4

<#
.SYNOPSIS
    One-command verification for the Dudu Desktop private v1 release.

.DESCRIPTION
    Runs the full build, test, relay, installer, performance, and end-to-end
    contract for a release candidate, then rejects the build if any of the
    private-use invariants are violated. Intended to run on a clean Windows
    11 24H2 x64 machine with Visual Studio 2026 (WinUI workload), the pinned
    .NET SDK, Node.js, and Inno Setup 7 installed — see docs/release.md for
    exact prerequisites.

    Deviation from the original task brief: this script runs
    `dotnet restore DuduDesktop.slnx` WITHOUT `--locked-mode`. This repository
    never commits `packages.lock.json` files (Central Package Management
    regenerates a lock file with a RID-specific entry whenever a project is
    restored, and a file generated on the macOS authoring host differs from
    one generated on the Windows release host). `--locked-mode` requires the
    checked-in lock file to byte-for-byte match the resolved graph, which
    would make this script fail on every clean checkout. docs/release.md
    repeats this explanation so anyone rerunning the release understands why
    the restore step is unlocked. Assert-NoTrackedGeneratedArtifacts below
    enforces that nothing generated — no lock file anywhere, nothing under
    work/ or outputs/ — is ever tracked.

    Every check below is written so it can fail: this script is the
    acceptance contract for the release, not a status report.
#>

$ErrorActionPreference = "Stop"
# Explicit (not just the pwsh >= 7.4 default): a nonzero exit from any native
# command below (dotnet, node, npm, nested pwsh) becomes a terminating error,
# so this script stops on the first real failure instead of plowing ahead.
$PSNativeCommandUseErrorActionPreference = $true

$RepoRoot = Split-Path -Parent $PSScriptRoot

function Assert-NoTrackedRawAssets {
    <#
        Raw source art (assets/raw/*.gif, *.png, …) must never be committed —
        only the placeholder assets/raw/.gitkeep may be tracked. Fails if git
        has any other tracked path under assets/raw.
    #>
    $tracked = git ls-files assets/raw
    $violations = $tracked | Where-Object { $_ -ne "assets/raw/.gitkeep" }
    if ($violations) {
        throw "Tracked file(s) under assets/raw besides .gitkeep: $($violations -join ', ')"
    }
}

function Assert-ManifestsPrivate {
    <#
        Every assets/sources/*.json provenance record, and the private-dudu
        pack manifest, must declare "privateUseOnly": true so the artwork is
        never mistaken for redistributable content.

        Deliberate exception: src/Dudu.App/Assets/Packs/fallback/manifest.json
        is "privateUseOnly": false. That pack is an originally generated
        neutral bear silhouette created for the local fallback case — it is
        not copied from Dudu's artwork and carries no third-party rights
        restriction, so asserting privateUseOnly: true on it would be false.
        docs/release.md documents this exception; this function intentionally
        does not inspect the fallback manifest.
    #>
    $targets = @()
    $targets += Get-ChildItem -Path (Join-Path $RepoRoot "assets/sources") -Filter "*.json" -File -ErrorAction SilentlyContinue
    $packsRoot = Join-Path $RepoRoot "src/Dudu.App/Assets/Packs"
    $targets += Get-ChildItem -Path $packsRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne "fallback" } |
        ForEach-Object { Join-Path $_.FullName "manifest.json" } |
        Where-Object { Test-Path $_ } |
        ForEach-Object { Get-Item $_ }

    if (-not $targets) {
        throw "No source manifests or pack manifests were found to check — the privateUseOnly gate would be vacuous."
    }

    $failures = @()
    foreach ($file in $targets) {
        $json = Get-Content -Raw -Path $file.FullName | ConvertFrom-Json
        if ($json.privateUseOnly -ne $true) {
            $failures += $file.FullName
        }
    }
    if ($failures) {
        throw "Manifest(s) missing 'privateUseOnly: true': $($failures -join ', ')"
    }
}

function Assert-RelayDepsInLockfile {
    <#
        Every dependency relay/package.json declares must already be
        represented in the committed relay/package-lock.json, so a fresh
        `npm ci` never silently pulls something that was never reviewed.
    #>
    Push-Location (Join-Path $RepoRoot "relay")
    try {
        npm ls --package-lock-only 1>$null
        if ($LASTEXITCODE -ne 0) {
            throw "relay dependencies are not fully represented in package-lock.json (npm ls --package-lock-only exited $LASTEXITCODE)"
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-NoTrackedGeneratedArtifacts {
    <#
        Nothing generated may be tracked by git: no packages.lock.json
        anywhere (every restore regenerates a RID-specific one, so a
        committed copy is already wrong on the next host), and nothing under
        work/ or outputs/, the two host-local scratch directories .gitignore
        excludes. Both checks read git's index, so they fail even when the
        working tree itself is clean. The parameters exist so
        tests/scripts/verify.tests.ps1 can drive both outcomes without
        staging real files; production callers pass nothing.
    #>
    param(
        [string[]]$TrackedLockFiles = @(git ls-files '*packages.lock.json'),
        [string[]]$TrackedScratchPaths = @(git ls-files work outputs)
    )

    $violations = @(@($TrackedLockFiles) + @($TrackedScratchPaths) |
        Where-Object { $_ } |
        Select-Object -Unique)
    if ($violations) {
        throw "Generated file(s) tracked by git (remove with ``git rm --cached``):`n$($violations -join "`n")"
    }
}

function Assert-CleanWorkingTree {
    <#
        Nothing this script (or the build it verifies) touches may leave a
        git working-tree change behind. work/ and outputs/ are ignored by
        .gitignore, so `git status --porcelain` never lists them at all; an
        untracked packages.lock.json left in a project directory does show
        up here, and fails the release.
    #>
    $violations = git status --porcelain
    if ($violations) {
        throw "Git working tree has unexpected change(s):`n$($violations -join "`n")"
    }
}

function Write-Sha256Sums {
    <#
        Writes a sha256sum-compatible manifest: lowercase hex hash, two
        spaces, then the file name (no path) — one line per input file.
    #>
    param(
        [Parameter(Mandatory = $true)][string[]]$Paths,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )
    $lines = foreach ($p in $Paths) {
        $hash = (Get-FileHash -Algorithm SHA256 -Path $p).Hash.ToLowerInvariant()
        "{0}  {1}" -f $hash, (Split-Path -Leaf $p)
    }
    $outDir = Split-Path -Parent $OutputPath
    if ($outDir -and -not (Test-Path $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }
    Set-Content -Path $OutputPath -Value $lines
}

# Dot-source guard, matching scripts/run-performance-gates.ps1: dot-sourcing
# this file (tests/scripts/verify.tests.ps1 does) must load the Assert-*
# functions without running a release verification.
# $MyInvocation.InvocationName is '.' only when dot-sourced.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

Push-Location $RepoRoot
try {
    Write-Host "== Toolchain versions ==" -ForegroundColor Cyan
    dotnet --version
    node --version

    Write-Host "== .NET restore / build / test ==" -ForegroundColor Cyan
    # Deviation from the brief: no --locked-mode. See the file header comment.
    dotnet restore DuduDesktop.slnx
    dotnet build DuduDesktop.slnx -c Release --no-restore
    dotnet test --solution DuduDesktop.slnx -c Release --no-build

    Write-Host "== Relay (Cloudflare Worker) ==" -ForegroundColor Cyan
    Push-Location relay
    try {
        npm ci
        npm run typecheck
        npm test -- --run
        npm run test:e2e
    }
    finally {
        Pop-Location
    }

    Write-Host "== Windows desktop build, installer, performance, end-to-end ==" -ForegroundColor Cyan
    pwsh scripts/publish-windows.ps1 -Version 1.0.0
    pwsh tests/installer/installer-smoke.ps1 `
        -Installer artifacts/DuduDesktop-1.0.0-win-x64-private.exe
    pwsh scripts/run-performance-gates.ps1 `
        -Executable artifacts/publish/win-x64/Dudu.App.exe `
        -Output artifacts/performance/release.json
    pwsh tests/e2e/private-note-flow.ps1 -RelayMode Local -NoteText "verification-secret-1042"

    Write-Host "== SHA-256 manifest ==" -ForegroundColor Cyan
    Write-Sha256Sums `
        -Paths @("artifacts/DuduDesktop-1.0.0-win-x64-private.exe") `
        -OutputPath "artifacts/SHA256SUMS.txt"

    Write-Host "== Private-use and working-tree invariants ==" -ForegroundColor Cyan
    Assert-NoTrackedRawAssets
    Assert-NoTrackedGeneratedArtifacts
    Assert-ManifestsPrivate
    Assert-RelayDepsInLockfile
    Assert-CleanWorkingTree

    Write-Host "verify.ps1: PASS" -ForegroundColor Green
}
finally {
    Pop-Location
}

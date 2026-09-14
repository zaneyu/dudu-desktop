<#
.SYNOPSIS
    One-shot setup of a fresh Windows 11 machine as a build host for the
    private installer, then (optionally) builds it.

.DESCRIPTION
    Installs the pinned .NET SDK, Node.js LTS, PowerShell 7 and Inno Setup 7
    through winget, restores the solution, and — unless -SkipBuild is given —
    runs scripts/publish-windows.ps1 followed by the installer smoke test.
    Run from an elevated PowerShell in the repository root:

        Set-ExecutionPolicy -Scope Process Bypass -Force
        .\scripts\bootstrap-windows-build.ps1

    Safe to re-run: winget skips packages that are already installed.

.PARAMETER Version
    Version passed through to publish-windows.ps1. Defaults to 1.0.0.

.PARAMETER SkipBuild
    Install tooling only.
#>
param(
    [string]$Version = "1.0.0",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw "winget was not found. Install 'App Installer' from the Microsoft Store, then re-run."
}

$packages = @(
    "Microsoft.DotNet.SDK.10",
    "OpenJS.NodeJS.LTS",
    "Microsoft.PowerShell",
    "JRSoftware.InnoSetup"
)
foreach ($id in $packages) {
    Write-Host "==> winget install $id"
    winget install --id $id --exact --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -notin 0, -1978335189) {   # -1978335189 = already installed
        throw "winget install $id failed with exit code $LASTEXITCODE."
    }
}

# Refresh PATH for this process so freshly installed tools resolve.
$env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [Environment]::GetEnvironmentVariable("Path", "User")

Push-Location $repoRoot
try {
    Write-Host "==> dotnet --version"
    dotnet --version
    Write-Host "==> dotnet restore"
    dotnet restore DuduDesktop.slnx
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

    if ($SkipBuild) { return }

    Write-Host "==> publish-windows.ps1 -Version $Version"
    pwsh -File (Join-Path $repoRoot "scripts/publish-windows.ps1") -Version $Version
    if ($LASTEXITCODE -ne 0) { throw "publish-windows.ps1 failed." }

    $installer = Join-Path $repoRoot "artifacts/DuduDesktop-$Version-win-x64-private.exe"
    Write-Host "==> installer smoke test: $installer"
    pwsh -File (Join-Path $repoRoot "tests/installer/installer-smoke.ps1") -Installer $installer
    if ($LASTEXITCODE -ne 0) { throw "installer smoke test failed." }

    Write-Host "Installer ready: $installer"
}
finally {
    Pop-Location
}

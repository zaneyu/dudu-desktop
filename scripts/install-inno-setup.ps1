<#
.SYNOPSIS
    Downloads and installs the pinned Inno Setup release after SHA-256 verification.

.DESCRIPTION
    The URL, filename, version, and digest live in installer/inno-setup-pinned.json. The
    downloaded executable is never started until its digest matches that checked-in record.
    Dot-sourcing this script loads Get-PinnedInnoSetupMetadata and
    Assert-PinnedInnoSetupFile for cross-platform contract tests without downloading anything.
#>
[CmdletBinding()]
param(
    [string]$DownloadDirectory
)

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$metadataPath = Join-Path $repoRoot "installer/inno-setup-pinned.json"

function Get-PinnedInnoSetupMetadata {
    $metadata = Get-Content -Raw -Path $metadataPath | ConvertFrom-Json
    foreach ($property in @("version", "fileName", "url", "sha256")) {
        if ([string]::IsNullOrWhiteSpace([string]$metadata.$property)) {
            throw "Inno Setup pin is missing '$property' in '$metadataPath'."
        }
    }

    if ($metadata.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Inno Setup pin has an invalid SHA-256 digest."
    }
    $uri = [Uri]$metadata.url
    if ($uri.Scheme -ne "https" -or $uri.Host -ne "github.com") {
        throw "Inno Setup pin must use an HTTPS GitHub release URL."
    }
    if ($metadata.fileName -notmatch '^innosetup-[0-9]+\.[0-9]+\.[0-9]+-x64\.exe$') {
        throw "Inno Setup pin has an unexpected x64 installer filename."
    }

    return $metadata
}

function Assert-PinnedInnoSetupFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Metadata
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Pinned Inno Setup download is missing at '$Path'."
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($actual, [string]$Metadata.sha256)) {
        throw "Pinned Inno Setup SHA-256 mismatch for '$Path' (expected $($Metadata.sha256), got $actual)."
    }
}

function Install-PinnedInnoSetup {
    param([string]$TargetDirectory)

    $metadata = Get-PinnedInnoSetupMetadata
    if ([string]::IsNullOrWhiteSpace($TargetDirectory)) {
        $TargetDirectory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    }
    New-Item -ItemType Directory -Path $TargetDirectory -Force | Out-Null
    $downloadPath = Join-Path $TargetDirectory $metadata.fileName

    Write-Host "Downloading Inno Setup $($metadata.version) from the pinned release URL."
    Invoke-WebRequest -Uri $metadata.url -OutFile $downloadPath
    Assert-PinnedInnoSetupFile -Path $downloadPath -Metadata $metadata
    Write-Host "Pinned Inno Setup digest verified: $($metadata.sha256)."

    $process = Start-Process $downloadPath -ArgumentList "/VERYSILENT", "/ALLUSERS", "/SUPPRESSMSGBOXES", "/NORESTART" -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Pinned Inno Setup installer exited with code $($process.ExitCode)."
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Install-PinnedInnoSetup -TargetDirectory $DownloadDirectory
}

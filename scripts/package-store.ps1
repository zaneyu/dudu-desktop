#Requires -Version 7.4
<#!
.SYNOPSIS
    Builds and validates a private Store MSIX package on a supported Windows host.

.DESCRIPTION
    This wrapper intentionally creates only Store-specific ignored artifacts. It
    does not submit packages or contact any remote service.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # This guard is for a future submission workflow. It does not submit a package.
    [switch]$RequirePartnerCenterIdentity,

    [string]$ExpectedPartnerCenterName,

    [string]$ExpectedPartnerCenterPublisher
)

$ErrorActionPreference = 'Stop'

if (-not [OperatingSystem]::IsWindows()) {
    throw 'Store package script requires Windows 11 x64 with the supported Visual Studio and Windows SDK installation.'
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Description
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not $resolvedPath.StartsWith("$resolvedParent$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must remain beneath $resolvedParent."
    }
    return $resolvedPath
}

function Write-MetadataText {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Text)
    Set-Content -LiteralPath (Join-Path $metadataDirectory $Name) -Value $Text -NoNewline -Encoding utf8
}

function Find-Tool {
    param([Parameter(Mandatory)][string]$FileName, [Parameter(Mandatory)][string[]]$Roots)
    foreach ($root in $Roots) {
        if ($root -and (Test-Path -LiteralPath $root)) {
            $tool = Get-ChildItem -LiteralPath $root -Filter $FileName -File -Recurse -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($tool) { return $tool.FullName }
        }
    }
    return $null
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$packageDirectory = Assert-ChildPath -Path (Join-Path $repoRoot 'artifacts/store-package') -Parent $artifactsRoot -Description 'Package output'
$metadataDirectory = Assert-ChildPath -Path (Join-Path $repoRoot 'artifacts/store-package-metadata') -Parent $artifactsRoot -Description 'Package metadata output'
$publishDirectory = Assert-ChildPath -Path (Join-Path $metadataDirectory 'publish') -Parent $metadataDirectory -Description 'Publish output'
$unpackDirectory = Assert-ChildPath -Path (Join-Path $metadataDirectory 'unpacked') -Parent $metadataDirectory -Description 'Package validation output'
$appProject = Join-Path $repoRoot 'src/Dudu.App/Dudu.App.csproj'
$manifestPath = Join-Path $repoRoot 'src/Dudu.App/Package.appxmanifest'

foreach ($directory in @($packageDirectory, $metadataDirectory)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $unpackDirectory -Force | Out-Null

[xml]$sourceManifest = Get-Content -Raw -LiteralPath $manifestPath
$sourceIdentity = $sourceManifest.Package.Identity
if ($RequirePartnerCenterIdentity) {
    if ([string]::IsNullOrWhiteSpace($ExpectedPartnerCenterName) -or [string]::IsNullOrWhiteSpace($ExpectedPartnerCenterPublisher)) {
        throw 'Partner Center identity validation requires the exact expected Name and Publisher values.'
    }
    if ($sourceIdentity.Name -eq 'DuduDesktop.Local.NonProduction' -or
        $sourceIdentity.Name -ne $ExpectedPartnerCenterName -or
        $sourceIdentity.Publisher -ne $ExpectedPartnerCenterPublisher) {
        throw 'Store-submission identity validation failed: replace the local manifest identity with the exact Partner Center Name and Publisher before enabling a submission workflow.'
    }
}

& dotnet --info | Set-Content -LiteralPath (Join-Path $metadataDirectory 'dotnet-info.txt') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed with exit code $LASTEXITCODE." }

& dotnet publish $appProject -c Release -r win-x64 --self-contained true `
    '-p:DuduStorePackage=true' "-p:Version=$Version" '-p:RuntimeIdentifier=win-x64' `
    '-p:AppxPackageSigningEnabled=false' "-p:PublishDir=$publishDirectory$([IO.Path]::DirectorySeparatorChar)" `
    "-p:AppxPackageDir=$packageDirectory$([IO.Path]::DirectorySeparatorChar)"
if ($LASTEXITCODE -ne 0) { throw "Store package publish failed with exit code $LASTEXITCODE." }

$packageCandidates = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse |
        Where-Object { $_.Extension -in @('.msix', '.msixupload', '.appx', '.appxupload') })
if ($packageCandidates.Count -ne 1) {
    throw "Expected exactly one Store package candidate under $packageDirectory; found $($packageCandidates.Count)."
}

$packageCandidate = $packageCandidates[0]
$artifactPath = Join-Path $packageDirectory ("DuduDesktop-$Version-win-x64$($packageCandidate.Extension)")
if ($packageCandidate.FullName -ne $artifactPath) {
    Move-Item -LiteralPath $packageCandidate.FullName -Destination $artifactPath
}

$windowsSdkRoots = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
    (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit')
)
$makeAppx = Find-Tool -FileName 'makeappx.exe' -Roots $windowsSdkRoots
if (-not $makeAppx) { throw 'makeappx.exe was not found in the supported Windows SDK installation.' }

& $makeAppx validate -p $artifactPath
$makeAppxValidateExitCode = $LASTEXITCODE
& $makeAppx unpack -p $artifactPath -d $unpackDirectory -o
$makeAppxUnpackExitCode = $LASTEXITCODE
Write-MetadataText -Name 'validation-summary.txt' -Text ("makeappx validate exit code: $makeAppxValidateExitCode`nmakeappx unpack exit code: $makeAppxUnpackExitCode`n")
if ($makeAppxValidateExitCode -ne 0 -or $makeAppxUnpackExitCode -ne 0) {
    throw 'Windows SDK package validation failed.'
}

[xml]$packagedManifest = Get-Content -Raw -LiteralPath (Join-Path $unpackDirectory 'AppxManifest.xml')
$packagedIdentity = $packagedManifest.Package.Identity
$expectedPackageVersion = "$Version.0"
if ($packagedIdentity.Name -ne $sourceIdentity.Name -or
    $packagedIdentity.Publisher -ne $sourceIdentity.Publisher -or
    $packagedIdentity.ProcessorArchitecture -ne 'x64' -or
    $packagedIdentity.Version -ne $expectedPackageVersion) {
    throw 'Packaged manifest identity, version, or architecture does not match the expected Store package contract.'
}

$requiredResources = @('PackageAssets\Logo.png', 'PackageAssets\Square150Logo.png')
foreach ($resource in $requiredResources) {
    if (-not (Test-Path -LiteralPath (Join-Path $unpackDirectory $resource))) {
        throw "Packaged resource is missing: $resource"
    }
}

$appCert = Find-Tool -FileName 'appcert.exe' -Roots $windowsSdkRoots
$appCertExitCode = 'not installed'
if ($appCert) {
    $appCertReport = Join-Path $metadataDirectory 'appcert-report.xml'
    & $appCert test -appxpackagepath $artifactPath -reportoutputpath $appCertReport
    $appCertExitCode = $LASTEXITCODE
    if ($appCertExitCode -ne 0) { throw "Windows App Certification Kit validation failed with exit code $appCertExitCode." }
}
Add-Content -LiteralPath (Join-Path $metadataDirectory 'validation-summary.txt') -Value "Windows App Certification Kit exit code: $appCertExitCode"

$hash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-MetadataText -Name 'SHA256SUMS.txt' -Text "$hash  $([IO.Path]::GetFileName($artifactPath))"
Write-MetadataText -Name 'package-version.txt' -Text $packagedIdentity.Version
Write-MetadataText -Name 'package-identity.txt' -Text ("Name=$($packagedIdentity.Name)`nPublisher=$($packagedIdentity.Publisher)`nProcessorArchitecture=$($packagedIdentity.ProcessorArchitecture)")
Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | ForEach-Object {
    $_.FullName.Substring($packageDirectory.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
} | Sort-Object | Set-Content -LiteralPath (Join-Path $metadataDirectory 'package-files.txt') -Encoding utf8

Write-Host "Store package: $artifactPath"
Write-Host "SHA-256: $hash"

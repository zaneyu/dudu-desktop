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

    # Hosted CI can build a local-identity acceptance artifact, but cannot run
    # WACK because GitHub-hosted runners have no interactive desktop. This
    # switch is deliberately explicit and is never valid for Partner Center.
    [switch]$AcceptanceOnly,

    # This guard is required when producing the separate package intended for submission.
    # It does not submit a package.
    [switch]$RequirePartnerCenterIdentity,

    [string]$ExpectedPartnerCenterName,

    [string]$ExpectedPartnerCenterPublisher
)

$ErrorActionPreference = 'Stop'

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

function Write-PreflightFailureEvidence {
    param([Parameter(Mandatory)][string]$Message)

    # Preflight must not overwrite the last known-good package metadata. Keep
    # failed-preflight evidence in ignored host-local scratch instead.
    $failureDirectory = Join-Path $repoRoot (Join-Path 'work/store-package-failures' ([Guid]::NewGuid().ToString('N')))
    New-Item -ItemType Directory -Path $failureDirectory -Force | Out-Null
    $sanitizedMessage = $Message -replace [regex]::Escape($repoRoot), '<repo>'
    $sanitizedMessage = $sanitizedMessage -replace '(?i)\b(token|secret|password)\b\s*[:=]\s*\S+', '$1=<redacted>'
    $sanitizedMessage = $sanitizedMessage -replace '[\r\n]+', ' '
    Set-Content -LiteralPath (Join-Path $failureDirectory 'validation-summary.txt') -Value ("Store package preflight failed: $sanitizedMessage") -NoNewline -Encoding utf8
    Write-Host "Store package preflight evidence: $failureDirectory"
}

function ConvertTo-SdkVersion {
    param([Parameter(Mandatory)][string]$Value)

    $match = [regex]::Match($Value, '(?<version>\d+\.\d+\.\d+\.\d+)')
    if (-not $match.Success) { return $null }
    try { return [Version]$match.Groups['version'].Value }
    catch { return $null }
}

function Resolve-WindowsSdkTools {
    param([switch]$RequireAppCert)

    $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
    $sdkBinRoot = Join-Path $windowsKitsRoot 'bin'
    $minimumSdkVersion = [Version]'10.0.26100.0'
    if (-not (Test-Path -LiteralPath $sdkBinRoot)) {
        throw "Supported Windows SDK bin directory is missing: $sdkBinRoot"
    }

    $supportedToolsets = @(
        Get-ChildItem -LiteralPath $sdkBinRoot -Directory | ForEach-Object {
            $sdkVersion = ConvertTo-SdkVersion -Value $_.Name
            $makeAppxPath = Join-Path $_.FullName 'x64\makeappx.exe'
            if ($sdkVersion -and $sdkVersion -ge $minimumSdkVersion -and (Test-Path -LiteralPath $makeAppxPath)) {
                [pscustomobject]@{ Version = $sdkVersion; MakeAppx = $makeAppxPath }
            }
        }
    )
    if ($supportedToolsets.Count -eq 0) {
        throw "makeappx.exe was not found at an x64 path in a Windows SDK $minimumSdkVersion or newer."
    }

    $selectedToolset = @($supportedToolsets | Sort-Object Version -Descending | Select-Object -First 1)[0]
    $selectedVersion = $selectedToolset.Version
    $selectedToolsets = @($supportedToolsets | Where-Object { $_.Version -eq $selectedVersion })
    if ($selectedToolsets.Count -ne 1) {
        throw "Windows SDK tool resolution is ambiguous for supported x64 SDK version $selectedVersion."
    }

    $appCertPath = $null
    if ($RequireAppCert) {
        $appCertPath = Join-Path $windowsKitsRoot 'App Certification Kit\appcert.exe'
        if (-not (Test-Path -LiteralPath $appCertPath)) {
            throw "Windows App Certification Kit (appcert.exe) is required at $appCertPath."
        }
        $appCertFileVersion = (Get-Item -LiteralPath $appCertPath).VersionInfo.FileVersion
        $appCertVersion = ConvertTo-SdkVersion -Value $appCertFileVersion
        if (-not $appCertVersion -or $appCertVersion -lt $selectedVersion) {
            throw "Windows App Certification Kit at $appCertPath is unsupported for the selected Windows SDK $selectedVersion."
        }
    }

    return [pscustomobject]@{
        MakeAppx = $selectedToolsets[0].MakeAppx
        AppCert = $appCertPath
        Version = $selectedVersion
    }
}

function Assert-StoreVersion {
    param([Parameter(Mandatory)][string]$Value)

    $components = $Value.Split('.')
    if ($components.Count -ne 3) {
        throw 'Store package version must use Major.Minor.Patch with three numeric components.'
    }

    $numbers = @()
    foreach ($component in $components) {
        try {
            $numbers += [Int64]::Parse($component, [Globalization.CultureInfo]::InvariantCulture)
        }
        catch {
            throw "Store package version component '$component' must be a non-negative integer."
        }
    }

    if ($numbers[0] -lt 1 -or $numbers[0] -gt 65535 -or
        $numbers[1] -lt 0 -or $numbers[1] -gt 65535 -or
        $numbers[2] -lt 0 -or $numbers[2] -gt 65535) {
        throw 'Store package version requires Major in 1..65535 and Minor/Patch in 0..65535; the package revision is fixed to 0.'
    }

}

function Assert-PartnerCenterIdentity {
    param(
        [Parameter(Mandatory)][System.Xml.XmlElement]$sourceIdentity,
        [Parameter(Mandatory)][string]$ExpectedPartnerCenterName,
        [Parameter(Mandatory)][string]$ExpectedPartnerCenterPublisher
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedPartnerCenterName) -or [string]::IsNullOrWhiteSpace($ExpectedPartnerCenterPublisher)) {
        throw 'Partner Center identity validation requires the exact expected Name and Publisher values.'
    }
    if ($sourceIdentity.Name -eq 'DuduDesktop.Local.NonProduction' -or
        $sourceIdentity.Name -ne $ExpectedPartnerCenterName -or
        $sourceIdentity.Publisher -ne $ExpectedPartnerCenterPublisher) {
        throw 'Store-submission identity validation failed: replace the local manifest identity with the exact Partner Center Name and Publisher before producing a submission package.'
    }
}

function Assert-AcceptanceOnlyIdentity {
    param([Parameter(Mandatory)][System.Xml.XmlElement]$sourceIdentity)

    if ($sourceIdentity.Name -ne 'DuduDesktop.Local.NonProduction' -or
        $sourceIdentity.Publisher -ne 'CN=Dudu Desktop Local Package, O=Dudu Desktop Local Development, C=US') {
        throw 'Acceptance-only packaging requires the local non-production identity and cannot produce a Partner Center submission package.'
    }
}

function Assert-PackageMode {
    param(
        [switch]$AcceptanceOnly,
        [switch]$RequirePartnerCenterIdentity
    )

    if ($AcceptanceOnly -eq $RequirePartnerCenterIdentity) {
        throw 'Specify exactly one package mode: -AcceptanceOnly or -RequirePartnerCenterIdentity.'
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$targetRuntimeIdentifier = 'win-x64'

# Validate every host, source, and tool input before deleting the last
# known-good package or metadata output. A failed preflight records sanitized
# evidence in ignored scratch and leaves those outputs untouched.
try {
    if (-not [OperatingSystem]::IsWindows()) {
        throw 'Store package script requires Windows 11 x64 with the supported Visual Studio and Windows SDK installation.'
    }
    Assert-StoreVersion -Value $Version
    $expectedPackageVersion = "$Version.0"
    Assert-PackageMode -AcceptanceOnly:$AcceptanceOnly -RequirePartnerCenterIdentity:$RequirePartnerCenterIdentity
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'Store package script requires a 64-bit Windows operating system.'
    }
    if (-not [Environment]::Is64BitProcess) {
        throw 'Store package script must run from a 64-bit PowerShell process.'
    }
    if ([Environment]::OSVersion.Version.Build -lt 26100) {
        throw 'Store package script requires Windows build 26100 or newer.'
    }
    if ($targetRuntimeIdentifier -ne 'win-x64') {
        throw 'Store package script requires the win-x64 target runtime.'
    }
    $dotnetVersion = & dotnet --version
    if ($LASTEXITCODE -ne 0) { throw "dotnet --version failed with exit code $LASTEXITCODE." }
    if ($dotnetVersion -ne '10.0.112') {
        throw "Store package script requires .NET SDK 10.0.112; found $dotnetVersion."
    }
    $artifactsRoot = Join-Path $repoRoot 'artifacts'
    $packageDirectory = Assert-ChildPath -Path (Join-Path $repoRoot 'artifacts/store-package') -Parent $artifactsRoot -Description 'Package output'
    $metadataDirectory = Assert-ChildPath -Path (Join-Path $repoRoot 'artifacts/store-package-metadata') -Parent $artifactsRoot -Description 'Package metadata output'
    $publishDirectory = Assert-ChildPath -Path (Join-Path $metadataDirectory 'publish') -Parent $metadataDirectory -Description 'Publish output'
    $unpackDirectory = Assert-ChildPath -Path (Join-Path $metadataDirectory 'unpacked') -Parent $metadataDirectory -Description 'Package validation output'
    $appProject = Join-Path $repoRoot 'src/Dudu.App/Dudu.App.csproj'
    $manifestPath = Join-Path $repoRoot 'src/Dudu.App/Package.appxmanifest'
    if (-not (Test-Path -LiteralPath $appProject)) { throw "Store app project is missing: $appProject" }
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Store package manifest is missing: $manifestPath" }
    [xml]$sourceManifest = Get-Content -Raw -LiteralPath $manifestPath
    $sourceIdentity = $sourceManifest.Package.Identity
    if (-not $sourceIdentity) { throw "Store package manifest has no Identity element: $manifestPath" }
    if ($AcceptanceOnly) {
        Assert-AcceptanceOnlyIdentity -sourceIdentity $sourceIdentity
    }
    else {
        Assert-PartnerCenterIdentity -sourceIdentity $sourceIdentity -ExpectedPartnerCenterName $ExpectedPartnerCenterName -ExpectedPartnerCenterPublisher $ExpectedPartnerCenterPublisher
    }
    $projectAssetsPath = Join-Path (Split-Path -Parent $appProject) 'obj\project.assets.json'
    if (-not (Test-Path -LiteralPath $projectAssetsPath)) {
        throw "Restored project assets are missing: $projectAssetsPath. Run dotnet restore before packaging."
    }
    $sdkTools = Resolve-WindowsSdkTools -RequireAppCert:(-not $AcceptanceOnly)
}
catch {
    Write-PreflightFailureEvidence -Message $_.Exception.Message
    throw
}

foreach ($directory in @($packageDirectory, $metadataDirectory)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $unpackDirectory -Force | Out-Null

& dotnet --info | Set-Content -LiteralPath (Join-Path $metadataDirectory 'dotnet-info.txt') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed with exit code $LASTEXITCODE." }

& dotnet publish $appProject -c Release -r win-x64 --self-contained true --no-restore `
    '-p:DuduStorePackage=true' "-p:Version=$Version" "-p:RuntimeIdentifier=$targetRuntimeIdentifier" `
    "-p:AppxPackageVersion=$expectedPackageVersion" '-p:AppxPackageSigningEnabled=false' "-p:PublishDir=$publishDirectory$([IO.Path]::DirectorySeparatorChar)" `
    "-p:AppxPackageDir=$packageDirectory$([IO.Path]::DirectorySeparatorChar)"
if ($LASTEXITCODE -ne 0) { throw "Store package publish failed with exit code $LASTEXITCODE." }

$packageFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse)
$unsupportedPackageCandidates = @($packageFiles | Where-Object { $_.Extension -in @('.appx', '.appxbundle', '.msixbundle') })
if ($unsupportedPackageCandidates.Count -ne 0) {
    throw "Store package output contains unsupported AppX or bundle format(s): $($unsupportedPackageCandidates.Count)."
}
$packageCandidates = @($packageFiles | Where-Object { $_.Extension -eq '.msix' })
if ($packageCandidates.Count -ne 1) {
    throw "Expected exactly one Store MSIX package candidate under $packageDirectory; found $($packageCandidates.Count)."
}

$packageCandidate = $packageCandidates[0]
$artifactPath = Join-Path $packageDirectory "DuduDesktop-$Version-win-x64.msix"
if ($packageCandidate.FullName -ne $artifactPath) {
    Move-Item -LiteralPath $packageCandidate.FullName -Destination $artifactPath
}

$makeAppx = $sdkTools.MakeAppx

& $makeAppx validate -p $artifactPath
$makeAppxValidateExitCode = $LASTEXITCODE
if ($makeAppxValidateExitCode -ne 0) {
    Write-MetadataText -Name 'validation-summary.txt' -Text ("makeappx validate exit code: $makeAppxValidateExitCode`nmakeappx unpack exit code: not run`n")
    throw 'Windows SDK package validation failed.'
}
& $makeAppx unpack -p $artifactPath -d $unpackDirectory -o
$makeAppxUnpackExitCode = $LASTEXITCODE
Write-MetadataText -Name 'validation-summary.txt' -Text ("makeappx validate exit code: $makeAppxValidateExitCode`nmakeappx unpack exit code: $makeAppxUnpackExitCode`n")
if ($makeAppxUnpackExitCode -ne 0) {
    throw 'Windows SDK package validation failed.'
}

[xml]$packagedManifest = Get-Content -Raw -LiteralPath (Join-Path $unpackDirectory 'AppxManifest.xml')
$packagedIdentity = $packagedManifest.Package.Identity
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

if ($AcceptanceOnly) {
    Add-Content -LiteralPath (Join-Path $metadataDirectory 'validation-summary.txt') -Value 'Windows App Certification Kit status: skipped (acceptance-only mode; not valid for Partner Center submission).'
}
else {
    $appCert = $sdkTools.AppCert
    $appCertReport = Join-Path $metadataDirectory 'appcert-report.xml'
    $appCertExitCode = $null
    $appCertFailure = $null
    try {
        & $appCert test -appxpackagepath $artifactPath -reportoutputpath $appCertReport
        $appCertExitCode = $LASTEXITCODE
    }
    catch {
        $appCertFailure = $_
        $appCertExitCode = 'launch failure'
    }
    Add-Content -LiteralPath (Join-Path $metadataDirectory 'validation-summary.txt') -Value "Windows App Certification Kit exit code: $appCertExitCode"
    if ($appCertFailure -or $appCertExitCode -ne 0) {
        throw "Windows App Certification Kit validation failed with exit code $appCertExitCode."
    }
}

$hash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-MetadataText -Name 'SHA256SUMS.txt' -Text "$hash  $([IO.Path]::GetFileName($artifactPath))"
Write-MetadataText -Name 'package-version.txt' -Text $packagedIdentity.Version
Write-MetadataText -Name 'package-identity.txt' -Text ("Name=$($packagedIdentity.Name)`nPublisher=$($packagedIdentity.Publisher)`nProcessorArchitecture=$($packagedIdentity.ProcessorArchitecture)")
Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | ForEach-Object {
    $_.FullName.Substring($packageDirectory.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
} | Sort-Object | Set-Content -LiteralPath (Join-Path $metadataDirectory 'package-files.txt') -Encoding utf8

Write-Host "Store package: $artifactPath"
Write-Host "SHA-256: $hash"

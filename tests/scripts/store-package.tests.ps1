<#
.SYNOPSIS
    Cross-platform source contract checks for the opt-in Store MSIX package.

.DESCRIPTION
    These checks parse the package manifest with its XML namespaces. They do
    not require MSIX tooling, a Store account, or a Windows host.
#>
$ErrorActionPreference = "Stop"

Import-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue
Import-Module Microsoft.PowerShell.Management -ErrorAction SilentlyContinue

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$script:FailureCount = 0
$script:CaseCount = 0

function Assert-True {
    param([string]$Name, [bool]$Condition)
    $script:CaseCount++
    if ($Condition) { Write-Host "PASS: $Name" } else { Write-Host "FAIL: $Name"; $script:FailureCount++ }
}

$appProjectPath = Join-Path $repoRoot "src/Dudu.App/Dudu.App.csproj"
$manifestPath = Join-Path $repoRoot "src/Dudu.App/Package.appxmanifest"
$appProject = Get-Content -Raw -LiteralPath $appProjectPath

Assert-True "package manifest exists" (Test-Path -LiteralPath $manifestPath)
Assert-True "app project keeps unpackaged mode as the default" (
    $appProject -match '<WindowsPackageType>None</WindowsPackageType>'
)
Assert-True "app project exposes an opt-in Store package property" (
    $appProject -match 'DuduStorePackage.*true'
)

if (Test-Path -LiteralPath $manifestPath) {
    [xml]$manifest = Get-Content -Raw -LiteralPath $manifestPath
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaceManager.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaceManager.AddNamespace('r', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')

    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespaceManager)
    $fullTrustCapability = $manifest.SelectNodes('/f:Package/f:Capabilities/r:Capability', $namespaceManager)
    $targetDeviceFamily = $manifest.SelectSingleNode('/f:Package/f:Dependencies/f:TargetDeviceFamily', $namespaceManager)

    Assert-True "manifest declares x64 identity" ($identity -and $identity.GetAttribute('ProcessorArchitecture') -eq 'x64')
    Assert-True "manifest declares full-trust application" (@($fullTrustCapability | ForEach-Object { $_.GetAttribute('Name') }) -contains 'runFullTrust')
    Assert-True "manifest declares a supported target device family" ($targetDeviceFamily -and $targetDeviceFamily.GetAttribute('Name') -eq 'Windows.Desktop')
    Assert-True "manifest identity is explicitly local-only until Partner Center values are supplied" (
        $identity -and $identity.GetAttribute('Name') -match '^DuduDesktop\.Local\.NonProduction$' -and
        $identity.GetAttribute('Publisher') -match '^CN=Dudu Desktop Local Package,'
    )
}

Write-Host ""
if ($script:FailureCount -gt 0) {
    Write-Host "store-package.tests.ps1: FAIL ($script:FailureCount of $script:CaseCount cases failed)"
    exit 1
}
Write-Host "store-package.tests.ps1: PASS ($script:CaseCount cases)"
exit 0

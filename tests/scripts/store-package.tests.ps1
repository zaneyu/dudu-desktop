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
[xml]$appProjectXml = $appProject
$storePackageCondition = "'`$(DuduStorePackage)' == 'true' and '`$(OS)' == 'Windows_NT'"
$storePackageProperties = @($appProjectXml.Project.PropertyGroup | Where-Object { $_.Condition -eq $storePackageCondition })

Assert-True "package manifest exists" (Test-Path -LiteralPath $manifestPath)
Assert-True "app project keeps unpackaged mode as the default" (
    $appProject -match '<WindowsPackageType>None</WindowsPackageType>'
)
Assert-True "app project has exactly one Windows-only Store package property group" ($storePackageProperties.Count -eq 1)
if ($storePackageProperties.Count -eq 1) {
    $storePackagePropertyGroup = $storePackageProperties[0]
    Assert-True "Store package mode uses MSIX" ($storePackagePropertyGroup.WindowsPackageType -eq 'MSIX')
    Assert-True "Store package mode enables MSIX tooling" ($storePackagePropertyGroup.EnableMsixTooling -eq 'true')
    Assert-True "Store package mode disables local signing" ($storePackagePropertyGroup.AppxPackageSigningEnabled -eq 'false')
    Assert-True "Store package mode never creates an Appx bundle" ($storePackagePropertyGroup.AppxBundle -eq 'Never')
    Assert-True "Store package mode does not generate an App Installer file" ($storePackagePropertyGroup.GenerateAppInstallerFile -eq 'false')
}

if (Test-Path -LiteralPath $manifestPath) {
    [xml]$manifest = Get-Content -Raw -LiteralPath $manifestPath
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaceManager.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaceManager.AddNamespace('r', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')

    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespaceManager)
    $fullTrustCapability = $manifest.SelectNodes('/f:Package/f:Capabilities/r:Capability', $namespaceManager)
    $targetDeviceFamily = $manifest.SelectSingleNode('/f:Package/f:Dependencies/f:TargetDeviceFamily', $namespaceManager)
    $visualElements = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application/uap:VisualElements', $namespaceManager)

    Assert-True "manifest declares x64 identity" ($identity -and $identity.GetAttribute('ProcessorArchitecture') -eq 'x64')
    Assert-True "manifest declares full-trust application" (@($fullTrustCapability | ForEach-Object { $_.GetAttribute('Name') }) -contains 'runFullTrust')
    Assert-True "manifest declares a supported target device family" ($targetDeviceFamily -and $targetDeviceFamily.GetAttribute('Name') -eq 'Windows.Desktop')
    Assert-True "manifest maps the 150px tile to its own logical logo family" (
        $visualElements -and $visualElements.GetAttribute('Square150x150Logo') -eq 'PackageAssets\Square150Logo.png'
    )
    foreach ($asset in @(
            @{ Name = 'Square150Logo.png'; Dimension = 150 },
            @{ Name = 'Square150Logo.scale-100.png'; Dimension = 150 },
            @{ Name = 'Square150Logo.scale-125.png'; Dimension = 188 },
            @{ Name = 'Square150Logo.scale-150.png'; Dimension = 225 },
            @{ Name = 'Square150Logo.scale-200.png'; Dimension = 300 },
            @{ Name = 'Square150Logo.scale-400.png'; Dimension = 600 }
        )) {
        $assetPath = Join-Path $repoRoot (Join-Path 'src/Dudu.App/PackageAssets' $asset.Name)
        Assert-True "150px logo family includes $($asset.Name)" (Test-Path -LiteralPath $assetPath)
        if (Test-Path -LiteralPath $assetPath) {
            $pngBytes = [IO.File]::ReadAllBytes($assetPath)
            $width = ($pngBytes[16] -shl 24) -bor ($pngBytes[17] -shl 16) -bor ($pngBytes[18] -shl 8) -bor $pngBytes[19]
            $height = ($pngBytes[20] -shl 24) -bor ($pngBytes[21] -shl 16) -bor ($pngBytes[22] -shl 8) -bor $pngBytes[23]
            Assert-True "150px logo family gives $($asset.Name) the expected dimensions" ($width -eq $asset.Dimension -and $height -eq $asset.Dimension)
        }
    }
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

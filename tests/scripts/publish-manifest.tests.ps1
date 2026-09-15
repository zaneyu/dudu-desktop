<#
.SYNOPSIS
    Cross-platform checks for the publish-tree allowlist and private-pack allowlist in
    scripts/publish-windows.ps1. Runs without dotnet, ISCC, or a real publish.

.DESCRIPTION
    The "allowed" samples cover every file shape seen in the Inno Setup file list of a real
    windows-latest publish (CI run 34903940347), including the one native PDB SkiaSharp ships.
    If a Windows App SDK / .NET bump adds a new legitimate shape, CI fails in
    Assert-PublishManifest naming the file; add a pattern there and a sample here.
#>
$ErrorActionPreference = "Stop"
Import-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue
Import-Module Microsoft.PowerShell.Management -ErrorAction SilentlyContinue

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
. (Join-Path $repoRoot "scripts/publish-windows.ps1")

$script:FailureCount = 0
$script:CaseCount = 0
function Assert-True {
    param([string]$Name, [bool]$Condition)
    $script:CaseCount++
    if ($Condition) { Write-Host "PASS: $Name" } else { Write-Host "FAIL: $Name"; $script:FailureCount++ }
}

$allowed = @(
    'Dudu.App.exe', 'createdump.exe', 'RestartAgent.exe',
    'Dudu.App.dll', 'Microsoft.WindowsAppRuntime.Bootstrap.dll', 'libSkiaSharp.dll',
    'Microsoft.UI.Xaml.winmd', 'Microsoft.UI.pri', 'Microsoft.UI.Xaml.Controls.pri',
    'Dudu.App.deps.json', 'Dudu.App.runtimeconfig.json', 'workloads.qnn.json',
    'libSkiaSharp.pdb',
    'en-us\Microsoft.ui.xaml.dll.mui', 'ca-Es-VALENCIA\Microsoft.UI.Xaml.Phone.dll.mui',
    'az-Latn-AZ\Microsoft.ui.xaml.dll.mui', 'kok-IN\Microsoft.ui.xaml.dll.mui',
    'Microsoft.UI.Xaml\Assets\NoiseAsset_256x256_PNG.png', 'Microsoft.UI.Xaml\Assets\map.html',
    'Assets\Packs\fallback\idle.png', 'Assets\Packs\fallback\manifest.json',
    'Assets/Packs/private-dudu/frames/base/blink/0000-536e8919d09d.png'
)
foreach ($path in $allowed) {
    Assert-True "allowlist accepts $path" (@(Get-UnexpectedPublishFiles -RelativePaths @($path)).Count -eq 0)
}

$rejected = @(
    'Dudu.App.pdb', 'build.log', 'setup.exe', 'Dudu.App.exe.bak', 'notes.txt', 'run.ps1',
    '.gitignore', 'Thumbs.db', 'desktop.ini', 'secrets.zip', 'appsettings.Development.xml',
    'Microsoft.UI.Xaml\Assets\other.html', 'en-us\evil.dll', 'Assets\Packs\fallback\idle.gif',
    'Assets\Packs\other-pack\idle.png', 'nested\dir\Dudu.App.dll', 'Assets\Packs\fallback\notes.md'
)
foreach ($path in $rejected) {
    Assert-True "allowlist rejects $path" (@(Get-UnexpectedPublishFiles -RelativePaths @($path)).Count -eq 1)
}

# End-to-end over a throwaway directory: stray file fails, clean tree writes a hashed manifest.
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("dudu-publish-manifest-" + [Guid]::NewGuid().ToString("N"))
try {
    $publishDir = Join-Path $tempRoot "publish"
    $metadataDir = Join-Path $tempRoot "metadata"
    New-Item -ItemType Directory -Path (Join-Path $publishDir "en-us"), $metadataDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $publishDir "Dudu.App.exe") -Value "exe"
    Set-Content -LiteralPath (Join-Path $publishDir "Dudu.App.dll") -Value "dll"
    Set-Content -LiteralPath (Join-Path $publishDir "en-us/Microsoft.ui.xaml.dll.mui") -Value "mui"

    Assert-PublishManifest -PublishDir $publishDir -MetadataDir $metadataDir
    $lines = @(Get-Content -LiteralPath (Join-Path $metadataDir "publish-manifest.txt"))
    Assert-True "clean tree writes one manifest line per file" ($lines.Count -eq 3)
    Assert-True "manifest lines are '<sha256>  <path>'" (@($lines | Where-Object { $_ -notmatch '^[0-9a-f]{64}  \S+$' }).Count -eq 0)
    Assert-True "manifest uses '/' separators" (($lines -join "`n") -match '  en-us/Microsoft\.ui\.xaml\.dll\.mui')

    Set-Content -LiteralPath (Join-Path $publishDir "build.log") -Value "stray"
    $threw = $false
    try { Assert-PublishManifest -PublishDir $publishDir -MetadataDir $metadataDir } catch { $threw = $_.Exception.Message -match 'build\.log' }
    Assert-True "stray file fails the publish check and is named" $threw

    # Private pack allowlist.
    $pack = Join-Path $tempRoot "pack"
    New-Item -ItemType Directory -Path (Join-Path $pack "frames/base/idle") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $pack "manifest.json") -Value '{"privateUseOnly": true}'
    Set-Content -LiteralPath (Join-Path $pack "frames/base/idle/0000-aa.png") -Value "png"
    $ok = $true
    try { Assert-PrivateReleaseAssetPack -PackRoot $pack } catch { $ok = $false }
    Assert-True "private pack with manifest + frames PNG passes" $ok

    Set-Content -LiteralPath (Join-Path $pack "frames/base/idle/source.psd") -Value "psd"
    $threw = $false
    try { Assert-PrivateReleaseAssetPack -PackRoot $pack } catch { $threw = $_.Exception.Message -match 'source\.psd' }
    Assert-True "private pack with a non-PNG file fails and names it" $threw
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:FailureCount -gt 0) {
    Write-Host "publish-manifest.tests.ps1: FAIL ($script:FailureCount of $script:CaseCount cases failed)"
    exit 1
}
Write-Host "publish-manifest.tests.ps1: PASS ($script:CaseCount cases)"
exit 0

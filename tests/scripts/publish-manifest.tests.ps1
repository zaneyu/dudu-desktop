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

    # Private audio pack allowlist and manifest contract. Keep these fixtures synthetic:
    # Task 1's real WAVs remain blocked pending waveform review.
    $audioRoot = Join-Path $tempRoot "audio"
    $audioPackIds = @('bubu-dudu-atata', 'tata-lala', 'dudu-lalala', 'dudu-atatata', 'dudu-yapapa')
    New-Item -ItemType Directory -Path $audioRoot -Force | Out-Null
    $audioPacks = @($audioPackIds | ForEach-Object {
        New-Item -ItemType Directory -Path (Join-Path $audioRoot $_) -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $audioRoot "$_/cue-01.wav") -Value "RIFF"
        [pscustomobject]@{
            packId = $_
            cues = @([pscustomobject]@{ cueId = 'cue-01'; filePath = "$_/cue-01.wav" })
        }
    })
    [pscustomobject]@{ schemaVersion = 1; privateUseOnly = $true; packs = $audioPacks } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $audioRoot 'manifest.json')
    $ok = $true
    try { Assert-PrivateAudioReleaseAssetPack -PackRoot $audioRoot } catch { $ok = $false }
    Assert-True "audio pack with all five private packs and referenced WAVs passes" $ok

    $audioAdversarialCases = @(
        @{ Name = 'MP3'; Path = 'tata-lala/bad.mp3'; Content = 'mp3'; Mutate = $null },
        @{ Name = 'root-level WAV'; Path = 'root.wav'; Content = 'wav'; Mutate = $null },
        @{ Name = 'path traversal entry'; Path = $null; Content = $null; Mutate = { param($manifest) $manifest.packs[0].cues[0].filePath = '../outside.wav } },
        @{ Name = 'second manifest'; Path = 'tata-lala/second-manifest.json'; Content = '{}'; Mutate = $null },
        @{ Name = 'non-private manifest'; Path = $null; Content = $null; Mutate = { param($manifest) $manifest.privateUseOnly = $false } },
        @{ Name = 'unknown pack id'; Path = $null; Content = $null; Mutate = { param($manifest) $manifest.packs[0].packId = 'unknown-pack' } },
        @{ Name = 'cue under wrong pack directory'; Path = 'unknown-pack/cue-01.wav'; Content = 'wav'; Mutate = { param($manifest) $manifest.packs[0].cues[0].filePath = 'unknown-pack/cue-01.wav' } },
        @{ Name = 'unreferenced WAV'; Path = 'tata-lala/unreferenced.wav'; Content = 'wav'; Mutate = $null }
    )
    foreach ($case in $audioAdversarialCases) {
        $caseRoot = Join-Path $tempRoot ("audio-" + ($case.Name -replace '[^A-Za-z0-9]', '-'))
        Copy-Item -LiteralPath $audioRoot -Destination $caseRoot -Recurse
        if ($null -ne $case.Path) {
            $caseFile = Join-Path $caseRoot $case.Path
            New-Item -ItemType Directory -Path (Split-Path -Parent $caseFile) -Force | Out-Null
            Set-Content -LiteralPath $caseFile -Value $case.Content
        }
        if ($null -ne $case.Mutate) {
            $caseManifest = Get-Content -Raw -LiteralPath (Join-Path $caseRoot 'manifest.json') | ConvertFrom-Json
            & $case.Mutate $caseManifest
            $caseManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $caseRoot 'manifest.json')
        }
        $threw = $false
        try { Assert-PrivateAudioReleaseAssetPack -PackRoot $caseRoot } catch { $threw = $true }
        Assert-True "audio pack rejects $($case.Name) before packaging" $threw
        Remove-Item -LiteralPath $caseRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Restore-generated package locks must not remain as untracked source-tree files after a
    # release. The publish script already copies them into release metadata; this verifies its
    # cleanup moves only the exact project lock files into ignored scratch storage.
    $lockRepo = Join-Path $tempRoot "lock-repo"
    $lockScratch = Join-Path $lockRepo "work/generated-package-locks"
    New-Item -ItemType Directory -Path (Join-Path $lockRepo "src/Dudu.Core"), (Join-Path $lockRepo "tests/Dudu.App.Tests"), (Join-Path $lockRepo "relay") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $lockRepo "src/Dudu.Core/packages.lock.json") -Value '{}'
    Set-Content -LiteralPath (Join-Path $lockRepo "tests/Dudu.App.Tests/packages.lock.json") -Value '{}'
    Set-Content -LiteralPath (Join-Path $lockRepo "relay/packages.lock.json") -Value '{}'

    $movedLocks = @(Move-GeneratedPackageLocksToScratch -RepoRoot $lockRepo -ScratchRoot $lockScratch)
    Assert-True "package-lock cleanup moves only source project locks" ($movedLocks.Count -eq 2)
    Assert-True "package-lock cleanup removes the source copies" (
        -not (Test-Path -LiteralPath (Join-Path $lockRepo "src/Dudu.Core/packages.lock.json")) -and
        -not (Test-Path -LiteralPath (Join-Path $lockRepo "tests/Dudu.App.Tests/packages.lock.json")))
    Assert-True "package-lock cleanup preserves unrelated relay files" (Test-Path -LiteralPath (Join-Path $lockRepo "relay/packages.lock.json"))
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

<#
.SYNOPSIS
    Publishes Dudu.App as a self-contained win-x64 build and compiles the
    private current-user installer with Inno Setup 7.1.0.

.PARAMETER Version
    Version stamped onto the published assembly via -p:Version. Defaults to
    1.0.0. The same value is passed to Inno Setup as /DAppVersion so the
    installer metadata and output filename stay aligned with the publish.
#>
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repoRoot "src/Dudu.App/Dudu.App.csproj"
$installerScriptPath = Join-Path $repoRoot "installer/DuduDesktop.iss"

if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "-Version must be a SemVer-like value such as 1.0.0 or 1.2.3-rc.1."
}

function Assert-PrivateReleaseAssetPack {
    param([Parameter(Mandatory = $true)][string]$PackRoot)

    $manifestPath = Join-Path $PackRoot "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Private release asset pack manifest is missing: '$manifestPath'."
    }

    try {
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    }
    catch {
        throw "Private release asset pack manifest is not valid JSON: '$manifestPath'."
    }
    if ($manifest.privateUseOnly -ne $true) {
        throw "Private release asset pack manifest must declare privateUseOnly: true: '$manifestPath'."
    }
    # installer/DuduDesktop.iss bundles this directory with a recursive wildcard, so the pack is
    # held to the same allowlist discipline as the publish tree: manifest.json at the root and
    # PNG frames under frames/, nothing else (no .psd/.gif sources, notes, or OS litter).
    $packFiles = @(Get-ChildItem -LiteralPath $PackRoot -Recurse -File -Force -ErrorAction SilentlyContinue |
        ForEach-Object { [IO.Path]::GetRelativePath($PackRoot, $_.FullName).Replace('\', '/') })
    $unexpected = @($packFiles | Where-Object {
        $_ -cne 'manifest.json' -and $_ -notmatch '^frames/(?:[A-Za-z0-9._-]+/)+[A-Za-z0-9._-]+\.png$'
    })
    if ($unexpected) {
        throw "Private release asset pack contains file(s) outside the allowlist (manifest.json, frames/**/*.png):`n$($unexpected -join "`n")"
    }
    if ($packFiles.Count -lt 2) {
        throw "Private release asset pack contains no asset files: '$PackRoot'."
    }
}

function Assert-PrivateAudioReleaseAssetPack {
    param([Parameter(Mandatory = $true)][string]$PackRoot)

    $manifestPath = Join-Path $PackRoot "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Private audio release pack manifest is missing: '$manifestPath'."
    }

    try {
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    }
    catch {
        throw "Private audio release pack manifest is not valid JSON: '$manifestPath'."
    }
    if ($manifest.privateUseOnly -ne $true) {
        throw "Private audio release pack manifest must declare privateUseOnly: true: '$manifestPath'."
    }

    $requiredPackIds = @('bubu-dudu-atata', 'tata-lala', 'dudu-lalala', 'dudu-atatata', 'dudu-yapapa')
    $packs = @($manifest.packs)
    $packIds = @($packs | ForEach-Object { [string]$_.packId })
    if ($packIds.Count -ne $requiredPackIds.Count -or
        (@($packIds | Where-Object { $_ -notin $requiredPackIds }).Count -gt 0) -or
        (@($requiredPackIds | Where-Object { $_ -notin $packIds }).Count -gt 0) -or
        (@($packIds | Group-Object | Where-Object { $_.Count -ne 1 }).Count -gt 0)) {
        throw "Private audio release pack manifest must contain exactly the five required pack ids: $($requiredPackIds -join ', ')."
    }

    # The installer bundles this directory recursively. Keep it to the manifest and WAV files
    # in pack subdirectories: no source downloads, alternate formats, nested manifests, or
    # root-level audio can reach the installer.
    $packFiles = @(Get-ChildItem -LiteralPath $PackRoot -Recurse -File -Force -ErrorAction SilentlyContinue |
        ForEach-Object { [IO.Path]::GetRelativePath($PackRoot, $_.FullName).Replace('\', '/') })
    $unexpected = @($packFiles | Where-Object {
        $_ -cne 'manifest.json' -and $_ -notmatch '^(?:[A-Za-z0-9._-]+/)+[A-Za-z0-9._-]+\.wav$'
    })
    if ($unexpected) {
        throw "Private audio release pack contains file(s) outside the allowlist (manifest.json, pack/**/*.wav):`n$($unexpected -join "`n")"
    }

    $referenced = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($pack in $packs) {
        foreach ($cue in @($pack.cues)) {
            $relative = ([string]$cue.filePath).Replace('\', '/')
            if ($relative -notmatch '^(?:[A-Za-z0-9._-]+/)+[A-Za-z0-9._-]+\.wav$') {
                throw "Private audio release pack contains an unsafe or non-WAV cue path: '$relative'."
            }
            if ($relative.Split('/')[0] -cne [string]$pack.packId) {
                throw "Private audio release pack cue path must begin with its owning pack id '$($pack.packId)': '$relative'."
            }
            if (-not $referenced.Add($relative)) {
                throw "Private audio release pack references the WAV more than once: '$relative'."
            }
            if (-not (Test-Path -LiteralPath (Join-Path $PackRoot $relative) -PathType Leaf)) {
                throw "Private audio release pack manifest references a missing WAV: '$relative'."
            }
        }
    }

    $actualWavs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @(Get-ChildItem -LiteralPath $PackRoot -Recurse -File -Filter '*.wav' -Force)) {
        [void]$actualWavs.Add(([IO.Path]::GetRelativePath($PackRoot, $file.FullName)).Replace('\', '/'))
    }
    $unreferenced = @($actualWavs | Where-Object { -not $referenced.Contains($_) })
    if ($unreferenced) {
        throw "Private audio release pack contains unreferenced WAV file(s):`n$($unreferenced -join "`n")"
    }
    $missing = @($referenced | Where-Object { -not $actualWavs.Contains($_) })
    if ($missing) {
        throw "Private audio release pack manifest references WAV file(s) that are not packaged:`n$($missing -join "`n")"
    }
    if ($referenced.Count -eq 0) {
        throw "Private audio release pack contains no referenced WAV files: '$PackRoot'."
    }
}

# Allowlist for the self-contained WinUI publish tree (relative paths, '/'-separated, matched
# case-insensitively). An exact per-file list is impractical: the tree is ~530 files whose
# names change with every Windows App SDK / .NET servicing bump (284 DLLs, 52 WinMDs, 86
# locale folders of .mui resources). Patterns instead pin the SHAPE of the tree — which file
# types may appear at the root, which named executables may exist, and which subfolders exist
# at all — so a stray log, script, archive, extra EXE, source file or unexpected folder fails
# the build before ISCC.exe bundles it. Derived from the ISCC file list of CI run 34903940347.
$script:PublishTreeAllowlist = @(
    # Root: managed/native binaries, WinRT metadata, PRI resource indexes, deps/runtimeconfig
    # and Windows App SDK workloads*.json.
    '^[A-Za-z0-9._-]+\.(?:dll|winmd|pri)$'
    '^[A-Za-z0-9._-]+\.json$'
    # Exactly these executables: the app, the .NET crash dump helper, and the Windows App SDK
    # restart agent. Any other .exe is a failure.
    '^(?:Dudu\.App|createdump|RestartAgent)\.exe$'
    # SkiaSharp's NuGet package ships its native PDB into the publish output. It is the only
    # PDB tolerated; managed symbols are embedded (DebugType=embedded).
    '^libSkiaSharp\.pdb$'
    # Windows App SDK XAML satellite resources, one folder per culture (e.g. ca-Es-VALENCIA).
    '^[a-z]{2,3}(?:-[a-z0-9]{2,8}){0,2}/[A-Za-z0-9._-]+\.mui$'
    # Windows App SDK XAML's own assets.
    '^Microsoft\.UI\.Xaml/Assets/(?:NoiseAsset_256x256_PNG\.png|map\.html)$'
    # App asset packs copied by the csproj.
    '^Assets/Packs/(?:fallback|private-dudu)/manifest\.json$'
    '^Assets/Packs/(?:fallback|private-dudu)/(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+\.png$'
    '^Assets/Audio/private-dudu/manifest\.json$'
    '^Assets/Audio/private-dudu/(?:[A-Za-z0-9._-]+/)+[A-Za-z0-9._-]+\.wav$'
)

function Get-UnexpectedPublishFiles {
    <#
        Returns every relative path in $RelativePaths that matches no allowlist pattern.
        Split out from Assert-PublishManifest so tests/scripts/publish-manifest.tests.ps1 can
        exercise the allowlist on any host without a real publish tree.
    #>
    param([string[]]$RelativePaths)

    @($RelativePaths | Where-Object {
        $path = $_.Replace('\', '/')
        -not ($script:PublishTreeAllowlist | Where-Object { $path -match $_ })
    })
}

function Assert-PublishManifest {
    <#
        Allowlist check: runs after `dotnet publish` and before ISCC.exe, so the installer's
        recursive [Files] wildcard only ever sees a tree whose every file matches
        $script:PublishTreeAllowlist. Fails listing each unexpected file. On success writes
        `<sha256>  <relative path>` for every file, sorted, to the release metadata directory
        so the exact bundled tree is recorded next to the installer's own hash.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$PublishDir,
        [Parameter(Mandatory = $true)][string]$MetadataDir
    )

    $entryExe = Join-Path $PublishDir "Dudu.App.exe"
    if (-not (Test-Path -LiteralPath $entryExe -PathType Leaf)) {
        throw "Publish output is missing the entry executable: '$entryExe'."
    }

    # -Force includes hidden/system files (.DS_Store, desktop.ini, Thumbs.db) in the check.
    $files = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Force -ErrorAction Stop)
    $relative = @($files | ForEach-Object { [IO.Path]::GetRelativePath($PublishDir, $_.FullName) })
    $unexpected = Get-UnexpectedPublishFiles -RelativePaths $relative
    if ($unexpected) {
        throw "Publish output contains $($unexpected.Count) file(s) outside the release allowlist (scripts/publish-windows.ps1 `$PublishTreeAllowlist):`n$($unexpected -join "`n")"
    }

    $manifestPath = Join-Path $MetadataDir "publish-manifest.txt"
    $files |
        ForEach-Object {
            $rel = [IO.Path]::GetRelativePath($PublishDir, $_.FullName).Replace('\', '/')
            "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $rel
        } |
        Sort-Object { $_.Substring(66) } |
        Set-Content -Path $manifestPath
}

function Invoke-DotnetCapture {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = (& dotnet @Arguments 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') exited with code $LASTEXITCODE.`n$output"
    }
    $output
}

function Write-ReleaseMetadata {
    <#
        Records what this installer was actually built from, into $MetadataDir (uploaded by CI
        as a separate release-metadata artifact):
          dotnet-info.txt      `dotnet --info` (exact SDK, runtimes, MSBuild, OS/RID)
          dotnet-packages.txt  `dotnet list package --include-transitive` for the solution
          lockfiles/**         every packages.lock.json the restore just generated
        Lock files are never committed (AGENTS.md: they are RID/host-specific and regenerated on
        every restore; Directory.Build.props sets RestorePackagesWithLockFile=true). Keeping the
        CI-generated copies with the release is the reproducibility record instead: a later build
        can diff its own generated lock files against these to prove the same package graph.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$MetadataDir
    )

    Push-Location $RepoRoot
    try {
        Set-Content -Path (Join-Path $MetadataDir "dotnet-info.txt") -Value (Invoke-DotnetCapture @("--info"))
        Set-Content -Path (Join-Path $MetadataDir "dotnet-packages.txt") `
            -Value (Invoke-DotnetCapture @("list", "DuduDesktop.slnx", "package", "--include-transitive"))
    }
    finally {
        Pop-Location
    }

    $lockRoot = Join-Path $MetadataDir "lockfiles"
    # Search only the .NET source roots: a full-repo walk would crawl relay/node_modules and work/.
    $lockFiles = @(foreach ($root in @("src", "tests", "tools")) {
            $rootPath = Join-Path $RepoRoot $root
            if (Test-Path -LiteralPath $rootPath) {
                Get-ChildItem -LiteralPath $rootPath -Recurse -File -Filter "packages.lock.json" -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName.Replace('\', '/') -notmatch '/(?:bin|obj)/' }
            }
        })
    if (-not $lockFiles) {
        throw "No packages.lock.json was generated under src/, tests/ or tools/ — RestorePackagesWithLockFile is expected to be on (Directory.Build.props)."
    }
    foreach ($lock in $lockFiles) {
        $destination = Join-Path $lockRoot ([IO.Path]::GetRelativePath($RepoRoot, $lock.FullName))
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $lock.FullName -Destination $destination
    }
}

function Move-GeneratedPackageLocksToScratch {
    <#
        Restore-generated packages.lock.json files are host/RID-specific and must not remain as
        untracked files in the source tree. Write-ReleaseMetadata has already copied the exact
        files into the release metadata artifact, so move the source copies into ignored work/
        scratch storage while preserving their project-relative paths.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ScratchRoot
    )

    $runRoot = Join-Path $ScratchRoot ("release-" + [Guid]::NewGuid().ToString("N"))
    $lockFiles = @(foreach ($root in @("src", "tests", "tools")) {
            $rootPath = Join-Path $RepoRoot $root
            if (Test-Path -LiteralPath $rootPath) {
                Get-ChildItem -LiteralPath $rootPath -Recurse -File -Filter "packages.lock.json" -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName.Replace('\', '/') -notmatch '/(?:bin|obj)/' }
            }
        })

    foreach ($lock in $lockFiles) {
        $relative = [IO.Path]::GetRelativePath($RepoRoot, $lock.FullName)
        $destination = Join-Path $runRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Move-Item -LiteralPath $lock.FullName -Destination $destination
        $destination
    }
}

# Dot-source guard (same convention as scripts/verify.ps1): dot-sourcing loads the functions
# above for tests/scripts/publish-manifest.tests.ps1 without publishing anything.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

$privatePackRoot = Join-Path $repoRoot "src/Dudu.App/Assets/Packs/private-dudu"
Assert-PrivateReleaseAssetPack -PackRoot $privatePackRoot
$privateAudioPackRoot = Join-Path $repoRoot "src/Dudu.App/Assets/Audio/private-dudu"
Assert-PrivateAudioReleaseAssetPack -PackRoot $privateAudioPackRoot

# Refuse cleanly, with no stack trace, if Inno Setup 7 is not installed.
$isccPath = Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"
if (-not (Test-Path $isccPath)) {
    throw "Inno Setup 7 was not found at '$isccPath'. Install Inno Setup 7.1.0, then re-run this script."
}

# Resolve the publish output directory and verify — before ever removing
# anything — that the resolved path is genuinely beneath this repository's
# artifacts directory. This is the one guard standing between a future edit
# and an accidental `Remove-Item -Recurse` outside artifacts/.
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$publishDir = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts/publish/win-x64"))
$artifactsPrefix = $artifactsRoot + [IO.Path]::DirectorySeparatorChar
if (-not $publishDir.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove '$publishDir': it does not resolve beneath '$artifactsRoot'."
}

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

dotnet publish $projectPath `
  -c Release -r win-x64 --self-contained true `
  -p:WindowsAppSDKSelfContained=true `
  -p:PublishSingleFile=false `
  -p:DebugType=embedded `
  -p:Version=$Version `
  -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish exited with code $LASTEXITCODE."
}

# Release metadata lives in its own directory (resolved and cleaned like the publish dir) so
# the installer artifact and the metadata artifact never mix.
$metadataDir = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts/release-metadata"))
if (-not $metadataDir.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove '$metadataDir': it does not resolve beneath '$artifactsRoot'."
}
if (Test-Path $metadataDir) {
    Remove-Item -Recurse -Force $metadataDir
}
New-Item -ItemType Directory -Path $metadataDir -Force | Out-Null

Assert-PublishManifest -PublishDir $publishDir -MetadataDir $metadataDir
Write-ReleaseMetadata -RepoRoot $repoRoot -MetadataDir $metadataDir
Move-GeneratedPackageLocksToScratch `
    -RepoRoot $repoRoot `
    -ScratchRoot (Join-Path $repoRoot "work/generated-package-locks") | Out-Null

& $isccPath "/DAppVersion=$Version" $installerScriptPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe exited with code $LASTEXITCODE."
}

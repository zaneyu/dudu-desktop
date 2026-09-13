<#
.SYNOPSIS
    Publishes Dudu.App as a self-contained win-x64 build and compiles the
    private current-user installer with Inno Setup 7.1.0.

.PARAMETER Version
    Version stamped onto the published assembly via -p:Version. Defaults to
    1.0.0, matching installer/DuduDesktop.iss's AppVersion and
    OutputBaseFilename.
#>
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repoRoot "src/Dudu.App/Dudu.App.csproj"
$installerScriptPath = Join-Path $repoRoot "installer/DuduDesktop.iss"

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

& $isccPath $installerScriptPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe exited with code $LASTEXITCODE."
}

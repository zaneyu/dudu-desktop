<#
.SYNOPSIS
    Installer smoke test for the private current-user Dudu Desktop
    installer: clean install, in-place upgrade, and both uninstall data
    choices.

.PARAMETER Installer
    Path to the built DuduDesktop-1.0.0-win-x64-private.exe.

.DESCRIPTION
    1. Silently installs for the current user and verifies Dudu.App.exe and
       the Start Menu shortcut exist, then runs --self-test and requires
       exit code 0.
    2. Writes a marker.txt file into the data root, reinstalls over the top
       (in-place upgrade) and verifies the marker survives.
    3. Uninstalls with data preserved, verifies the marker and data root
       still survive, then reinstalls and verifies the marker again.
    4. Uninstalls with /DELETEUSERDATA=1 and verifies the data root
       (marker.txt included) is gone.

    On a host where $Installer does not exist, Start-Process in step 1
    throws immediately — this is the installer's TDD "RED" evidence before
    any installer artifact has been built.
#>
param(
    [Parameter(Mandatory)]
    [string]$Installer
)

$ErrorActionPreference = "Stop"

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\DuduDesktop"
$dataRoot = Join-Path $env:LOCALAPPDATA "DuduDesktop"
$exePath = Join-Path $installRoot "Dudu.App.exe"
$uninstallerPath = Join-Path $installRoot "unins000.exe"
$shortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Dudu Desktop.lnk"
$markerPath = Join-Path $dataRoot "marker.txt"

function Install-DuduDesktopSilently {
    Start-Process $Installer -ArgumentList "/VERYSILENT", "/CURRENTUSER", "/NORESTART" -Wait
    if ($LASTEXITCODE -ne 0) {
        throw "Installer exited with code $LASTEXITCODE."
    }
}

function Uninstall-DuduDesktopSilently {
    param([switch]$DeleteUserData)

    if (-not (Test-Path $uninstallerPath)) {
        throw "Uninstaller was not found at '$uninstallerPath'."
    }

    $arguments = @("/VERYSILENT", "/NORESTART")
    if ($DeleteUserData) {
        $arguments += "/DELETEUSERDATA=1"
    }

    Start-Process $uninstallerPath -ArgumentList $arguments -Wait
    if ($LASTEXITCODE -ne 0) {
        throw "Uninstaller exited with code $LASTEXITCODE."
    }
}

function Invoke-SelfTest {
    & $exePath --self-test
    if ($LASTEXITCODE -ne 0) {
        throw "Installed application self-test failed."
    }
}

# --- Phase 1: clean install ---
Install-DuduDesktopSilently
if (-not (Test-Path $exePath)) {
    throw "Application executable was not installed for the current user."
}
if (-not (Test-Path $shortcutPath)) {
    throw "Start Menu shortcut is missing."
}

Invoke-SelfTest

# --- Phase 2: marker survives an in-place upgrade ---
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
Set-Content -Path $markerPath -Value "dudu-installer-smoke-marker" -NoNewline

Install-DuduDesktopSilently
if (-not (Test-Path $markerPath)) {
    throw "marker.txt did not survive an in-place upgrade."
}

# --- Phase 3: uninstall with data preserved, then reinstall ---
Uninstall-DuduDesktopSilently
if (Test-Path $exePath) {
    throw "Application executable was not removed by uninstall."
}
if (-not (Test-Path $markerPath)) {
    throw "marker.txt did not survive an uninstall with data preserved."
}

Install-DuduDesktopSilently
if (-not (Test-Path $markerPath)) {
    throw "marker.txt did not survive a reinstall after a data-preserving uninstall."
}

# --- Phase 4: uninstall with /DELETEUSERDATA=1 removes only the data root ---
Uninstall-DuduDesktopSilently -DeleteUserData
if (Test-Path $exePath) {
    throw "Application executable was not removed by uninstall."
}
if (Test-Path $dataRoot) {
    throw "Data root '$dataRoot' was not removed by an uninstall with /DELETEUSERDATA=1."
}

Write-Host "Installer smoke test passed: clean install, upgrade, and both uninstall data choices all behaved correctly."
exit 0

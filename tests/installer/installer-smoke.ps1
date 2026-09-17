<#
.SYNOPSIS
    Installer smoke test for the private current-user Dudu Desktop
    installer: clean install, in-place upgrade, and both uninstall data
    choices.

.PARAMETER Installer
    Path to the built versioned installer.

.PARAMETER ExerciseRunningApp
    Launches the installed app before the upgrade and requires the installer to close that
    exact process. Use this on an interactive Windows desktop; headless CI may omit it.

.PARAMETER ExerciseNormalLaunch
    Launches the installed app normally (without --self-test), requires it to remain alive,
    checks that startup-failure.log was not written, and closes the exact process cleanly. Use
    this on an interactive Windows desktop; headless CI must omit it.

.DESCRIPTION
    1. Creates an isolated per-run install/data root and a sentinel outside that root. Silently
       installs for the current user and verifies Dudu.App.exe and
       the Start Menu shortcut exist, then runs --self-test and requires
       exit code 0.
    2. Writes a marker.txt file into the isolated data root, optionally starts the installed app,
       reinstalls over the top (in-place upgrade), and verifies the marker survives.
    3. Uninstalls with data preserved, verifies the marker and data root
       still survive, then reinstalls and verifies the marker again.
    4. Uninstalls with /DELETEUSERDATA=1 and verifies only the isolated data root is gone while
       the sentinel outside the test root survives.

    On a host where $Installer does not exist, Start-Process in step 1
    throws immediately — this is the installer's TDD "RED" evidence before
    any installer artifact has been built.
#>
param(
    [Parameter(Mandatory)]
    [string]$Installer,

    [switch]$ExerciseRunningApp,

    [switch]$ExerciseNormalLaunch
)

$ErrorActionPreference = "Stop"

$Installer = [IO.Path]::GetFullPath($Installer)
$runId = [Guid]::NewGuid().ToString("N")
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "dudu-installer-smoke-$runId"
$installRoot = Join-Path $testRoot "install"
$dataRoot = Join-Path $testRoot "data\DuduDesktop"
$sentinelRoot = Join-Path ([IO.Path]::GetTempPath()) "dudu-installer-smoke-sentinel-$runId"
$sentinelPath = Join-Path $sentinelRoot "sentinel.txt"
$exePath = Join-Path $installRoot "Dudu.App.exe"
$uninstallerPath = Join-Path $installRoot "unins000.exe"
$shortcutPath = Join-Path $testRoot "start-menu\Dudu Desktop.lnk"
$markerPath = Join-Path $dataRoot "marker.txt"
# The "launch at sign-in" shortcut StartupRegistrationService owns. Review I7:
# uninstall must delete it whatever the user chose about keeping their data,
# otherwise every later sign-in tries to launch a removed executable. The
# installer has a test-only environment override so this smoke test never
# overwrites or deletes the operator's real Startup shortcut.
$startupShortcutPath = Join-Path $testRoot "startup\Dudu Desktop Companion.lnk"

if (($installRoot -notlike "$testRoot\*") -or ($dataRoot -notlike "$testRoot\*")) {
    throw "Installer smoke paths did not resolve beneath the isolated test root."
}
if ($sentinelRoot -like "$testRoot\*") {
    throw "Installer smoke sentinel must be outside the isolated test root."
}

$previousDataRoot = [Environment]::GetEnvironmentVariable("DUDU_DATA_ROOT", "Process")
$previousStartupShortcut = [Environment]::GetEnvironmentVariable("DUDU_STARTUP_SHORTCUT_PATH", "Process")
$previousStartMenuShortcut = [Environment]::GetEnvironmentVariable("DUDU_START_MENU_SHORTCUT_DIR", "Process")
$runningApp = $null
$normalApp = $null

New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
New-Item -ItemType Directory -Path $sentinelRoot -Force | Out-Null
Set-Content -Path $sentinelPath -Value "operator-sentinel-$runId" -NoNewline
[Environment]::SetEnvironmentVariable("DUDU_DATA_ROOT", $dataRoot, "Process")
[Environment]::SetEnvironmentVariable("DUDU_STARTUP_SHORTCUT_PATH", $startupShortcutPath, "Process")
[Environment]::SetEnvironmentVariable("DUDU_START_MENU_SHORTCUT_DIR", (Split-Path -Parent $shortcutPath), "Process")

function Install-DuduDesktopSilently {
    # Start-Process -Wait never sets $LASTEXITCODE; read the process's own exit code.
    $directoryArgument = "/DIR=`"$installRoot`""
    $process = Start-Process $Installer -ArgumentList "/VERYSILENT", "/CURRENTUSER", "/NORESTART", $directoryArgument -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Installer exited with code $($process.ExitCode)."
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

    $process = Start-Process $uninstallerPath -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Uninstaller exited with code $($process.ExitCode)."
    }
}

function New-StartupShortcutStandIn {
    # Stands in for the shortcut the app itself would have written when the
    # recipient turned "launch at sign-in" on. The uninstaller only ever tests
    # for and deletes this path, so a plain file is a faithful stand-in.
    $startupDirectory = Split-Path -Parent $startupShortcutPath
    if (-not (Test-Path $startupDirectory)) {
        New-Item -ItemType Directory -Path $startupDirectory -Force | Out-Null
    }
    Set-Content -Path $startupShortcutPath -Value "dudu-installer-smoke-startup-shortcut" -NoNewline
}

function Start-InstalledAppForUpgrade {
    $process = Start-Process $exePath -PassThru
    try { $process.WaitForInputIdle(10000) | Out-Null } catch { }
    Start-Sleep -Seconds 3
    if ($process.HasExited) {
        throw "Installed application exited before the running-app upgrade check could run."
    }
    return $process
}

function Assert-ProcessClosedByInstaller {
    param([Parameter(Mandatory)][System.Diagnostics.Process]$Process)

    if (-not $Process.WaitForExit(30000)) {
        throw "Installer did not close the app process it was asked to upgrade."
    }
    Write-Host "Running-app upgrade check passed: Inno Setup closed the owned app process."
}

function Start-InstalledAppForNormalLaunch {
    $process = Start-Process $exePath -PassThru
    try { $process.WaitForInputIdle(10000) | Out-Null } catch { }
    Start-Sleep -Seconds 3
    if ($process.HasExited) {
        Write-SelfTestDiagnostics
        throw "Installed application exited during the normal-launch check."
    }

    $startupFailureLog = Join-Path $dataRoot "logs\startup-failure.log"
    if (Test-Path -LiteralPath $startupFailureLog) {
        Write-SelfTestDiagnostics
        throw "Normal launch wrote startup-failure.log."
    }
    return $process
}

function Close-NormalLaunchApp {
    param([Parameter(Mandatory)][System.Diagnostics.Process]$Process)

    try { $Process.CloseMainWindow() | Out-Null } catch { }
    if (-not $Process.WaitForExit(30000)) {
        throw "Normal-launch application did not close cleanly within 30 s."
    }
}

function Assert-SentinelSurvives {
    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
        throw "Sentinel outside the test root was deleted."
    }
    if ((Get-Content -Raw -LiteralPath $sentinelPath) -ne "operator-sentinel-$runId") {
        throw "Sentinel outside the test root was modified."
    }
}

function Invoke-SelfTest {
    # Dudu.App is a WinExe: the call operator returns immediately and never sets
    # $LASTEXITCODE, so start it as a process and read its own exit code.
    $process = Start-Process $exePath -ArgumentList "--self-test" -PassThru
    if (-not $process.WaitForExit(120000)) {
        $process.Kill()
        Write-SelfTestDiagnostics
        throw "Installed application self-test did not exit within 120 s."
    }
    if ($process.ExitCode -ne 0) {
        Write-SelfTestDiagnostics
        throw "Installed application self-test failed with exit code $($process.ExitCode)."
    }
}

function Write-SelfTestDiagnostics {
    # self-test.log never contains note text or key material (SelfTestFileLogger).
    $selfTestLog = Join-Path $dataRoot "logs\self-test.log"
    if (Test-Path $selfTestLog) {
        Write-Host "--- self-test.log ---"
        Get-Content $selfTestLog | Write-Host
    } else {
        Write-Host "self-test.log was not written at '$selfTestLog'."
    }
    $since = (Get-Date).AddMinutes(-5)
    Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in @(".NET Runtime", "Application Error", "Windows Error Reporting") } |
        Select-Object -First 5 |
        ForEach-Object {
            Write-Host "--- $($_.ProviderName) $($_.TimeCreated) ---"
            Write-Host $_.Message
        }
}

try {
    # --- Phase 1: clean isolated install ---
    Install-DuduDesktopSilently
    if (-not (Test-Path $exePath)) { throw "Application executable was not installed in the isolated root." }
    if (-not (Test-Path $shortcutPath)) { throw "Start Menu shortcut is missing." }
    $privateManifestPath = Join-Path $installRoot "Assets\Packs\private-dudu\manifest.json"
    if (-not (Test-Path $privateManifestPath)) { throw "Private asset manifest was not packaged." }
    $privateManifest = Get-Content -Raw $privateManifestPath | ConvertFrom-Json
    if ($privateManifest.privateUseOnly -ne $true) { throw "Installed private asset manifest is not privateUseOnly: true." }

    Invoke-SelfTest

    if ($ExerciseNormalLaunch) {
        $normalApp = Start-InstalledAppForNormalLaunch
        Close-NormalLaunchApp -Process $normalApp
        $normalApp = $null
    }

    # --- Phase 2: marker survives an in-place upgrade ---
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    Set-Content -Path $markerPath -Value "dudu-installer-smoke-marker" -NoNewline
    if ($ExerciseRunningApp) { $runningApp = Start-InstalledAppForUpgrade }
    Install-DuduDesktopSilently
    if ($null -ne $runningApp) {
        Assert-ProcessClosedByInstaller -Process $runningApp
        $runningApp = $null
    }
    if (-not (Test-Path $markerPath)) { throw "marker.txt did not survive an in-place upgrade." }

    # --- Phase 3: uninstall with data preserved, then reinstall ---
    New-StartupShortcutStandIn
    Uninstall-DuduDesktopSilently
    if (Test-Path $exePath) { throw "Application executable was not removed by uninstall." }
    if (-not (Test-Path $markerPath)) { throw "marker.txt did not survive an uninstall with data preserved." }
    if (Test-Path $startupShortcutPath) { throw "Startup shortcut was not removed by data-preserving uninstall." }

    Install-DuduDesktopSilently
    if (-not (Test-Path $markerPath)) { throw "marker.txt did not survive a reinstall after a data-preserving uninstall." }

    # --- Phase 4: data deletion is isolated and cannot reach the outside sentinel ---
    New-StartupShortcutStandIn
    Uninstall-DuduDesktopSilently -DeleteUserData
    if (Test-Path $exePath) { throw "Application executable was not removed by uninstall." }
    if (Test-Path $dataRoot) { throw "Isolated data root was not removed by /DELETEUSERDATA=1." }
    if (Test-Path $startupShortcutPath) { throw "Startup shortcut was not removed by /DELETEUSERDATA=1." }
    Assert-SentinelSurvives

    Write-Host "Installer smoke test passed: isolated install, running-app upgrade option, asset manifest, both uninstall data choices, and outside sentinel preservation all behaved correctly."
    exit 0
}
finally {
    if ($null -ne $normalApp -and -not $normalApp.HasExited) {
        try { $normalApp.CloseMainWindow() | Out-Null } catch { }
        try { $normalApp.WaitForExit(10000) | Out-Null } catch { }
    }
    if ($null -ne $runningApp -and -not $runningApp.HasExited) {
        try { $runningApp.CloseMainWindow() | Out-Null } catch { }
        try { $runningApp.WaitForExit(10000) | Out-Null } catch { }
    }
    [Environment]::SetEnvironmentVariable("DUDU_DATA_ROOT", $previousDataRoot, "Process")
    [Environment]::SetEnvironmentVariable("DUDU_STARTUP_SHORTCUT_PATH", $previousStartupShortcut, "Process")
    [Environment]::SetEnvironmentVariable("DUDU_START_MENU_SHORTCUT_DIR", $previousStartMenuShortcut, "Process")
    # These paths are generated uniquely by this run and are never operator data.
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $sentinelRoot -Recurse -Force -ErrorAction SilentlyContinue
}

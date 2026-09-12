# Task 12 report

Implemented the Windows companion lifecycle/tray surface from the Task 12 brief.

## Delivered

- `SingleInstanceCoordinator`: exact `Local\\DuduDesktop.App.v1` mutex, SHA-256 SID-hashed activation pipe, current-user pipe ACL/identity, bounded two-second activation, one-byte validation, cancellation, listener recovery, and abandoned-mutex recovery.
- `FullscreenDetector`: extended-frame-versus-nearest-monitor comparison with two-pixel tolerance, shell/desktop/cloaked/minimized/Dudu exclusions, and conservative native-failure behavior.
- `PausePolicy`: one-hour, local tomorrow 07:00 with timezone/DST conversion, fullscreen-only, indefinite, expiry normalization, and stale suppression rejection.
- `GlobalHotkeyService`: validated modifier/key grammar, default `Ctrl+Alt+D`, generated `RegisterHotKey`/`UnregisterHotKey` integration, replacement rollback, message dispatch, and idempotent disposal.
- `TrayIconService`: generated CsWin32 `Shell_NotifyIcon`/`NOTIFYICONDATAW`, command contract, taskbar recreation, and owner-thread cleanup.
- `StartupRegistrationService`: current-user Startup `.lnk`, installed executable plus `--background`, atomic replacement, idempotent enable/disable, no registry/admin path.
- `AppLifecycleCoordinator`: lock/suspend hide, gated unlock/resume welcome-back, fullscreen hide/restore without welcome-back, display/taskbar recovery, and AppHost lifecycle adapter.
- `tests/Dudu.WindowsHarness`: `--scenario single-instance` now launches primary/secondary processes, creates one real overlay in the primary, and records one `OpenHome` activation.

`Preferences.HidePetDuringFullscreen` and its default/schema/repository persistence were already present on the requested base commit and are consumed by the lifecycle coordinator.

## Verification

Using the repository SDK at `work/dotnet-sdk` and packaging-disabled flags:

- `src/Dudu.App/Dudu.App.csproj`: build passed with 0 warnings/errors.
- `tests/Dudu.App.Tests/Dudu.App.Tests.csproj`: Windows x64 build passed with 0 warnings/errors.
- `tests/Dudu.WindowsHarness/Dudu.WindowsHarness.csproj`: Windows x64 build passed with 0 warnings/errors.
- Added deterministic policy, fullscreen, hotkey, single-instance, startup, tray, lifecycle, malformed-payload, expiry, rollback, disposal, and concurrency-oriented seam tests.

Runtime test execution and the two-process harness could not run on this host: it is macOS arm64, while these projects target Windows x64 and call Windows-only APIs. Wine is installed but is an x86 executable and cannot run on this arm64 host (`bad CPU type`). No Windows runtime result is being claimed here.

The full solution build was not used as evidence because forcing `win-x64` without restoring all unrelated net10 projects produces expected missing-assets targets; the three Task 12 deliverables above were built directly with the required flags.

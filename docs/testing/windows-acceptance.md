# Windows acceptance evidence

Task 23's release gates — accessibility, UI automation, an eight-hour stability run, and the
performance thresholds — all require real Windows UI Automation, a published `win-x64`
`Dudu.App.exe`, and WinUI's `XamlCompiler.exe`. None of that is available on the macOS host this
row of work was authored on: FlaUI needs a live Windows desktop session, the performance and
long-run harness scenarios call `OperatingSystem.IsWindows()` and exit immediately (codes 3–6) on
any other OS, and `dotnet publish -r win-x64` for a WinUI project invokes a Windows-only native
compiler that crashes under Rosetta/macOS.

Every row below is **pending** — a placeholder for the tester who runs this checklist on a real
Windows build host, not a claim that the run happened. No file under `artifacts/performance/` or
`artifacts/stability/` exists in this repository, and none should be committed once they do (see
`.gitignore` / the repo's hard constraint against committing `artifacts/`).

## Evidence table

| Scenario | Status | Windows build | Timestamp | Tester | Evidence path |
| --- | --- | --- | --- | --- | --- |
| `AccessibilityTests.Every_actionable_settings_control_has_name_and_keyboard_focus` | pending | — | — | — | `artifacts/ui-tests/accessibility.trx` |
| `AccessibilityTests.Text_scaling_at_two_hundred_percent_has_no_clipped_primary_actions` | pending | — | — | — | `artifacts/ui-tests/accessibility.trx` |
| `FullJourneyTests.Full_companion_journey_persists_every_change_across_a_restart` | pending | — | — | — | `artifacts/ui-tests/full-journey.trx` |
| Performance gate (`scripts/run-performance-gates.ps1 -Executable artifacts/publish/win-x64/Dudu.App.exe`) | pending | — | — | — | `artifacts/performance/release.json` |
| Eight-hour stability run (`dotnet run --project tests/Dudu.WindowsHarness -c Release -- --scenario long-run --hours 8 --output artifacts/stability/eight-hour.json`) | pending | — | — | — | `artifacts/stability/eight-hour.json` |
| Full solution test pass (`dotnet test DuduDesktop.slnx -c Release`) | pending | — | — | — | `artifacts/test-results/` |

Fill in "Windows build" with the OS build number (`winver`) and Dudu version actually exercised,
"Timestamp" in UTC, "Tester" with a name or handle, and "Evidence path" with where the actual
artifact was saved once that run happens — update each row in place rather than appending new
ones, so this file always reflects the latest run per scenario.

## How to run each gate on Windows

1. Publish a release candidate: `pwsh scripts/publish-windows.ps1` (produces
   `artifacts/publish/win-x64/Dudu.App.exe`).
2. UI automation and accessibility:
   ```powershell
   dotnet test tests/Dudu.UiTests/Dudu.UiTests.csproj --filter "FullyQualifiedName~AccessibilityTests|FullyQualifiedName~FullJourneyTests" `
     -e DUDU_UI_TEST_EXE=artifacts/publish/win-x64/Dudu.App.exe
   ```
   Both test classes skip themselves (`Assert.Skip`) on a non-Windows OS or when
   `DUDU_UI_TEST_EXE` is unset/not found, rather than failing — a skip in this run means the
   environment variable or OS check, not the product, stopped the test from executing.
3. Performance gate:
   ```powershell
   pwsh scripts/run-performance-gates.ps1 -Executable artifacts/publish/win-x64/Dudu.App.exe -Output artifacts/performance/release.json
   ```
   Exits 0 only when every threshold in `PerformanceThresholds`
   (`tests/Dudu.WindowsHarness/PerformanceScenario.cs`) and its PowerShell mirror
   (`Test-PerformanceThresholds` in `scripts/run-performance-gates.ps1`) passes: startup under
   3000 ms, idle CPU under 1%, animation CPU under 3%, peak working set under 200 MB, and an
   allocation slope no greater than 1 MB/hour.
4. Eight-hour stability run:
   ```powershell
   dotnet run --project tests/Dudu.WindowsHarness -c Release -- --scenario long-run --hours 8 --output artifacts/stability/eight-hour.json
   ```
   Exits 0 only when the process never crashed, GDI and USER handle counts never grew more than
   5%, working set never grew faster than 1 MB/hour, and no duplicate reminder-occurrence or
   remote-note row was found (`StabilityThresholds.Evaluate` in
   `tests/Dudu.WindowsHarness/LongRunScenario.cs`). The duplicate-row check can only catch a
   regression in the database's own primary-key guarantees — see that file's doc comment for why
   it cannot detect an application-level "same notification shown twice" bug.
5. Full solution pass: `dotnet test DuduDesktop.slnx -c Release`.

## What was verified on the macOS authoring host instead

Since none of the five gates above could run here, this is what stands in as evidence that the
code is at least well-formed and internally consistent before a Windows tester runs the real
thing:

- `src/Dudu.App`, `tests/Dudu.App.Tests`, `tests/Dudu.UiTests`, and `tests/Dudu.WindowsHarness`
  all build with 0 warnings and 0 errors under the macOS stub-build flags (see this task's report
  for the exact command).
- `scripts/run-performance-gates.ps1` parses cleanly under `pwsh` and its pure threshold function,
  `Test-PerformanceThresholds`, is unit-tested independently of any real executable or Windows API
  by `tests/scripts/run-performance-gates.tests.ps1`, runnable on this host.
- `AccessibilityTests` and `FullJourneyTests` are written in full against the product's real,
  stable automation IDs and skip themselves cleanly (not via a stub) on this host.
- Every XAML file this task touched was checked for well-formedness with a Python `xml.dom.minidom`
  parse, since this host cannot run WinUI's own XAML compiler.
- `src/Dudu.App/Themes/*.xaml` were already high-contrast compliant before this task and were not
  touched by it.

None of this substitutes for the actual Windows run. Treat every `pending` row above as exactly
that until a tester replaces it with a real result.

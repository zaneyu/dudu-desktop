# Startup Reliability Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the private Windows companion fail visibly and recoverably during startup, while adding tests that exercise the real launch path rather than only the WinUI-free self-test.

**Architecture:** Keep the existing App → Infrastructure → Core dependency direction. Fix the WinUI application resource initialization at the entry point, make startup phases observable and diagnostics non-throwing, make crash-loop safe mode omit native overlay construction, and make database restore replacement transactional across validation, migration, rollback, and interrupted-startup reconciliation.

**Tech Stack:** .NET 10.0.112, WinUI 3/Windows App SDK, C# xUnit/Microsoft Testing Platform, SQLite, PowerShell installer smoke tests, GitHub Actions Windows validation.

**Spec:** `docs/handoffs/2026-09-17-post-audit-defects.md` plus the startup audit findings recorded in the task conversation.

## Global Constraints

- Private Windows 11 x64 companion and private relay; do not publish private URLs, artwork, installer, or checksums.
- Preserve `Dudu.App` → `Dudu.Infrastructure` → `Dudu.Core`; no reverse references or UI dependencies in Core.
- Do not commit `bin/`, `obj/`, `artifacts/`, `work/`, `outputs/`, `node_modules/`, `.wrangler/`, `relay/dist/`, or `packages.lock.json`.
- Mac builds are stub-mode evidence only; real WinUI, DPAPI, installer, and normal-launch acceptance remain Windows-only.
- New behavior requires regression coverage in the closest test project.
- Preserve unrelated existing user changes in the dirty worktree.

---

### Task 1: Prove and repair application resource initialization

**Files:**
- Modify: `tests/Dudu.App.Tests/Ui/XamlContractTests.cs`
- Modify: `src/Dudu.App/App.xaml.cs:26-34`

**Interfaces:**
- Consumes the existing `App.xaml` merged dictionaries and WinUI-generated `Application.InitializeComponent()` method.
- Produces an application startup contract that loads `Themes/Colors.xaml` and `Themes/Controls.xaml` before any Settings page is created.

- [ ] **Step 1: Write the failing source contract test** asserting `App.xaml.cs` calls `InitializeComponent();` in the `App()` constructor and `App.xaml` contains both merged dictionaries.
- [ ] **Step 2: Run the focused App.Tests contract test and verify it fails because the constructor omits the call.**
- [ ] **Step 3: Add `InitializeComponent();` as the first operation in the constructor, before dispatcher access and bootstrap setup.**
- [ ] **Step 4: Run the focused test and the Mac-safe App build; verify both pass.**

### Task 2: Make startup phase failures diagnosable and self-test failures observable

**Files:**
- Modify: `src/Dudu.App/Hosting/WindowsCompanionProductionComposition.cs`
- Modify: `src/Dudu.App/App.xaml.cs`
- Modify: `src/Dudu.App/Hosting/AppPaths.cs`
- Modify: `src/Dudu.App/Hosting/SelfTestRunner.cs`
- Test: `tests/Dudu.App.Tests/Hosting/StartupFailureLoggerTests.cs`
- Test: `tests/Dudu.App.Tests/Hosting/SelfTestRunnerTests.cs`

**Interfaces:**
- Keep the existing public startup runner surface; add internal phase wrappers or phase-aware exception context without exposing secrets.
- `StartupFailureLogger.Record` and the App failure path must never throw, including invalid environment paths.
- `--self-test` must write a failure record and return a nonzero code instead of leaving an unobserved task.

- [ ] **Step 1: Add failing tests for invalid `DUDU_DATA_ROOT`, self-test exception handling, and phase names in startup failure records.**
- [ ] **Step 2: Run the focused tests and capture the failing behavior.**
- [ ] **Step 3: Move path resolution behind a best-effort diagnostic fallback, catch self-test failures at the App entry boundary, and pass phase labels from production composition checkpoints.**
- [ ] **Step 4: Run focused tests and verify logs contain only sanitized phase/exception data.**

### Task 3: Make crash-loop safe mode genuinely minimal

**Files:**
- Modify: `src/Dudu.App/Hosting/WindowsCompanionProductionComposition.cs`
- Modify: `src/Dudu.App/Hosting/WindowsCompanionRuntime.cs` or its actual runtime file if the type is nested/relocated
- Test: `tests/Dudu.App.Tests/Hosting/ProductionStartupContractTests.cs`

**Interfaces:**
- Safe mode must skip overlay window host, Skia composer, animation engine, tray, hotkey, and remote-sync attachment.
- Safe mode must still initialize the local database and expose a recoverable Settings path.
- Normal mode behavior and runtime disposal remain unchanged.

- [ ] **Step 1: Add a failing composition contract test proving safe mode does not construct the overlay runtime.**
- [ ] **Step 2: Run the focused test and verify the current composition still constructs `WindowsCompanionRuntime` in safe mode.**
- [ ] **Step 3: Introduce a minimal safe-mode runtime or composition branch that owns only local recovery/settings services.**
- [ ] **Step 4: Add disposal and startup tests for the minimal branch; run the focused App test project.**

### Task 4: Make restore replacement transactional across SQLite migration failures

**Files:**
- Modify: `src/Dudu.Infrastructure/Data/DatabaseBackupService.cs`
- Modify: `src/Dudu.Infrastructure/Data/Database.cs` if cache invalidation needs a narrow seam
- Modify: `tests/Dudu.Infrastructure.Tests/Data/DatabaseTests.cs`

**Interfaces:**
- A restore either leaves the migrated backup as canonical or restores the exact pre-restore main/WAL/SHM set.
- SQLite migration exceptions must enter the same rollback path as filesystem exceptions.
- Only documented corruption codes may cause checkpoint continuation; do not classify SQLite notice/warning codes as corruption.

- [ ] **Step 1: Add failing tests for migration failure after replacement, code 27 not being treated as corruption, and paired sidecar rollback/reconciliation.**
- [ ] **Step 2: Run the focused Infrastructure tests and verify failures.**
- [ ] **Step 3: Broaden the inner transactional catch to the rollback-eligible database/filesystem failures, preserve the original and rollback exceptions, and remove code 27 from the corruption list.**
- [ ] **Step 4: Run the focused Infrastructure tests and verify the canonical database and sidecars survive every injected failure.**

### Task 5: Repair the current migration regression and verify older-schema behavior

**Files:**
- Modify: `tests/Dudu.Infrastructure.Tests/Data/DatabaseTests.cs`
- Inspect/modify only if required: `src/Dudu.Infrastructure/Data/Migrations/0005_evening_routines.sql`, `src/Dudu.Infrastructure/Data/MigrationRunner.cs`

**Interfaces:**
- The rollback test injects a version above the current production schema and asserts the actual current schema version.
- Version 5 evening-routine columns remain durable and idempotent.

- [ ] **Step 1: Change the failing rollback test injection to version 6 and expected schema to the actual current version, without removing migration 5.**
- [ ] **Step 2: Run the focused test and confirm it passes.**
- [ ] **Step 3: Run all direct Infrastructure tests and confirm no older-schema restore regression remains.**

### Task 6: Add a real normal-launch installer smoke gate

**Files:**
- Modify: `tests/installer/installer-smoke.ps1`
- Modify: `.github/workflows/windows-installer.yml`
- Modify: `docs/testing/windows-acceptance.md`
- Test: `tests/scripts/release-contract.tests.ps1`

**Interfaces:**
- Keep `--self-test` as a headless prerequisite check.
- Add an interactive Windows normal-launch check that starts the installed executable without `--self-test`, waits for the process to remain alive, verifies startup-failure log absence, and closes it cleanly.
- CI must not pretend a headless runner exercised WinUI; the interactive check is explicit and documented.

- [ ] **Step 1: Add failing release-contract assertions for the normal-launch invocation, bounded wait, and cleanup.**
- [ ] **Step 2: Run the release contract test and verify the existing smoke script fails the new contract.**
- [ ] **Step 3: Implement the optional normal-launch phase with deterministic process cleanup and diagnostics.**
- [ ] **Step 4: Run PowerShell contract tests on Mac if available and document the Windows-only acceptance command.**

### Task 7: Final verification and generated-file hygiene

**Files:**
- No production changes expected.
- Move only generated `packages.lock.json` files produced by restore into `work/generated-package-locks/`.

- [ ] **Step 1: Run `git diff --check` and inspect the complete diff for privacy/dependency-direction violations.**
- [ ] **Step 2: Run relay typecheck/tests, Mac-safe Windows-targeted builds, direct Core/Infrastructure executables, and release contract tests.**
- [ ] **Step 3: Confirm no generated files are tracked or newly untracked outside ignored scratch locations.**
- [ ] **Step 4: Report exact passing results and the remaining Windows-only validation required for the girlfriend’s desktop.**

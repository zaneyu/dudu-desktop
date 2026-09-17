# Handoff: post-audit defects to fix (2026-09-17)

Date: 2026-09-17
Base: `main` @ `fea788e` plus the uncommitted audit-fix diff in the working tree
Scope: the confirmed-but-unfixed findings from the 2026-09-17 multi-agent audit.
Out of scope (user decision): key-repair policy, pre-rotation pairing codes, legacy
ownerless ciphertext migration policy.

Run all verification Mac-safe per AGENTS.md. `Dudu.App.Tests` cannot execute on macOS;
its execution gate is Windows CI. Core/Infrastructure tests run via the generated xUnit
executables with `DOTNET_ROOT=$(git rev-parse --show-toplevel)/work/dotnet-sdk`.

## 1. Backup restore aborts when the current database is corrupt (Infrastructure)

- File: `src/Dudu.Infrastructure/Data/DatabaseBackupService.cs`
- Bug 1 (line ~192): `RestoreAsync` calls `CheckpointCurrentDatabaseAsync` before installing
  the replacement. If the current DB is corrupt (`SqliteException`, error 11/26), restore
  aborts — exactly when a valid backup is needed.
- Bug 2 (line ~102): `RestoreLatestValidAsync` returns failure after the first `RestoreFailed`,
  never trying older backups. Only `Restored` breaks the loop; a transient `RestoreFailed`
  on the newest backup blocks every older valid one.
- Bug 3 (line ~229): rollback `File.Move(stagedPath, canonicalPath)` lacks `overwrite: true`;
  if the failed install left a partial file at the canonical path the rollback itself throws
  (wrapped into `AggregateException`, data left unrecovered).

Fix: distinguish corrupt-current-DB from locking/permission errors in
`CheckpointCurrentDatabaseAsync` — for corruption, keep the old main file as a recovery set
(e.g. rename sidecars aside) and continue installing the validated backup instead of
aborting; still fail for locks/permissions. In `RestoreLatestValidAsync`, continue to older
candidates on `RestoreFailed` (stop only on `Restored`). Add `overwrite: true` to the
rollback move. Do NOT blanket-ignore all checkpoint failures.

Tests: `tests/Dudu.Infrastructure.Tests/Data/DatabaseTests.cs` — restore over an unreadable
corrupt current DB; `RestoreLatestValidAsync` skipping an invalid newest backup; rollback
path with a pre-existing canonical file.

## 2. Restore of an older-schema backup leaves the process unmigrated (Infrastructure)

- Files: `src/Dudu.Infrastructure/Data/DatabaseBackupService.cs:156-161` (accepts older
  schema), `src/Dudu.Infrastructure/Data/Database.cs:141-149` (static init-task cache),
  `src/Dudu.App/ViewModels/CompanionFeatureContext.cs:94-104` (immediate reload of
  preferences, which needs newer columns).
- Bug: after restoring a genuine older-schema backup mid-session, the static coordinator
  keeps its completed initialization task, so existing and NEW `Database` instances at that
  path skip migrations; the caller's next query fails. Restore can also succeed then surface
  an error from the reload.
- Fix: inside `RestoreAsync`, after installing the replacement (still holding the
  maintenance lease), run migrations on the new canonical DB and only then release callers —
  or invalidate the static initialization while maintenance owns the path. Merely
  constructing a new `Database` does not fix the static cache.
- Test: build a DB with an older migration subset, restore it, then exercise preferences +
  remote-envelope repositories through both an existing and a freshly constructed instance.

## 3. Crash-interrupted restore can leave no canonical database (Infrastructure)

- File: `src/Dudu.Infrastructure/Data/DatabaseBackupService.cs:202-238`
- Bug: between `StageFile` (canonical -> `.restore-old-*`) and `_moveFile(temporaryPath,
  canonicalPath)` a process kill leaves no database at the canonical path. The catch-based
  rollback cannot run; next startup's `ReadWriteCreate` creates a fresh empty DB silently.
- Fix: crash-safe replacement — write a durable sidecar marker (or use the existing
  `.restore-old-*`/`.restore-*` naming as the recovery set) and add a startup reconciliation
  step that runs BEFORE opening the canonical path in create mode: if a staged-old set exists
  without a canonical DB, move it back; if a `.restore-*` temp exists, validate and install
  or discard it.
- Test: preconstruct the interrupted-restore filesystem states; assert a fresh process
  recovers the old or new DB and never initializes an empty one.

## 4. Local wipe does not stop the remote-sync loop first (App wiring, ~3 lines)

- File: `src/Dudu.App/Hosting/WindowsCompanionProductionComposition.cs:472-474`
- Bug: `deleteLocalDataAsync` calls `LocalDataMaintenanceService.DeleteAllUserDataAsync`
  directly. A running `RemoteSyncService` poll/registration in flight can recreate rows or
  secrets after (or during) the wipe; the maintenance lease only excludes DB connections,
  not sync operations, and the relay re-registration gate is `_registrationGate` in
  `RemoteSyncService`, which Infrastructure cannot close from inside the wipe.
- Fix: stop the sync service before wiping, e.g.
  `deleteLocalDataAsync: async token => { await services.GetRequiredService<RemoteSyncService>().StopAsync(token); await services.GetRequiredService<LocalDataMaintenanceService>().DeleteAllUserDataAsync(token); }`
  Check `RemoteSyncHostAdapter` at the bottom of the same file for the correct stop surface
  (prefer the `IAppHostRemoteSync`/adapter `DisposeAsync`/stop path if `StopAsync` is not
  public on the service).
- Test: an Infrastructure-level test with a barrier-held in-flight poll + registration;
  after `DeleteAllUserDataAsync` completes and the barriers release, no user rows, secrets
  (`*.bin`), or notifications may be recreated. Extend
  `tests/Dudu.Infrastructure.Tests/Data/LocalDataMaintenanceTests.cs`.

## 5. Wipe leaves abandoned DPAPI temp files (Infrastructure)

- Files: `src/Dudu.Infrastructure/Security/DpapiSecretStore.cs:55,68-81,231-248`,
  `src/Dudu.Infrastructure/Data/LocalDataMaintenanceService.cs:96-98`
- Bug: interrupted secret writes leave `*.tmp` protected temp files; the wipe deletes only
  `*.bin`, so decryptable-under-same-user artifacts survive a "complete" wipe.
- Fix: after draining secret writers, delete the store's temp naming pattern (or the whole
  app-owned secrets directory if exclusive), and report incomplete cleanup instead of
  declaring success on failure.
- Test: extend `LocalDataMaintenanceTests.cs` with an abandoned `.tmp` fixture; assert both
  canonical and temp artifacts are gone.

## 6. Focus extension budget is inconsistent (Core — needs one schema field)

- File: `src/Dudu.Core/Focus/FocusService.cs:171-198` (extension), model
  `src/Dudu.Core/Models/FocusSession.cs` (has `StartedUtc`, `EndsUtc`,
  `RemainingWhenPaused`), persistence
  `src/Dudu.Infrastructure/Data/Repositories/FocusSessionRepository.cs` +
  `src/Dudu.Infrastructure/Data/Migrations/0001_initial.sql:82`
  (`remaining_when_paused_ticks`).
- Bug A: running branch compares `EndsUtc - StartedUtc > MaxDuration` (line ~176), counting
  all paused wall-clock time; a 25-min session paused >24h then resumed can't take a 5-min
  extension.
- Bug B: paused branch checks only `RemainingWhenPaused + extension` (line ~185), so a
  23-hour session that already consumed 22 hours can still take a 24-hour remainder —
  bypassing the per-session cap.
- Fix: add a consumed-focus-time or total-budget field (`consumed_ticks` alongside
  `remaining_when_paused_ticks`): new migration `000X_focus_budget.sql`, model field,
  repository read/write + CAS predicate, and budget math in `ExtendAsync` (and
  `ResumeAsync` accrual) that excludes paused time but counts consumed focus time. Keep
  `MaxDuration` semantics; both branches must use the one consistent budget.
- Tests: `tests/Dudu.Core.Tests/Focus/FocusServiceTests.cs` — overnight pause then small
  extension succeeds; long consumed session + large paused extension is rejected; extension
  during pause still allowed up to the budget.

## 7. Relay scheduling timestamps: parser-contract mismatch (relay, small)

- Files: `relay/src/security/envelopeValidation.ts:122-134`,
  `relay/src/db/messages.ts:263,311,335` (`julianday(...)` eligibility).
- Bug: `deliverAfterUtc` is validated only via `Date.parse` + real-calendar check for
  `createdUtc`; an offset spelling JS accepts but SQLite `julianday` rejects (e.g. RFC-style)
  is stored raw and the message stays ineligible until expiry. The existing test
  `compares offset deliver-after timestamps by instant...` covers `-14:00` ISO offsets only.
- Fix (minimal): extend validation to require the same strict ISO-8601 shape already
  enforced for `createdUtc` (`STRICT_ISO8601_Z_PATTERN` — see
  `relay/src/security/envelopeValidation.ts:138`), applied to `deliverAfterUtc` when
  non-null, plus the `isRealCalendarInstant` round-trip check. Reject others as
  `EnvelopeSemanticError`. Do not add a normalized second column (keeps envelope hash
  integrity).
- Test: `relay/test/messages.spec.ts` — an accepted `deliverAfterUtc` must produce the same
  eligibility instant; a non-strict spelling is rejected 422.

## Verification gate for the next agent

1. `relay/`: `npm run typecheck`; `DUDU_DOTNET=<repo>/work/dotnet-sdk/dotnet npm test --
   --run` (currently 96/96); `npm run test:e2e` (currently 48/48).
2. `dotnet restore` + Core/Infrastructure test executables (currently 142/142 and
   168 total, 0 failed, 8 expected skips).
3. Windows-only: push to `main`, watch `gh run watch --exit-status` on
   `windows-installer.yml` — `Dudu.App.Tests` executes only there.
4. Move any regenerated `packages.lock.json` into
   `work/generated-package-locks/<label>/`; never commit them.

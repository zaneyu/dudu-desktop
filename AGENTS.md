# Dudu Desktop Companion — agent instructions

These instructions apply to work in this repository. Read them before changing
code, testing, or producing a release artifact.

## Project scope and non-negotiables

- This is a private-use Windows 11 x64 desktop companion and a private
  Cloudflare relay/sender system.
- Treat the relay URL, sender page, Dudu artwork, asset packs, installer, and
  release checksums as private. Do not publish them, add them to a public
  marketplace, or paste them into issues, logs, or chat.
- Preserve the dependency direction: `Dudu.App` → `Dudu.Infrastructure` →
  `Dudu.Core`. Do not introduce reverse references or UI dependencies into
  Core.
- Do not commit generated files: `bin/`, `obj/`, `artifacts/`, `work/`,
  `outputs/`, `node_modules/`, `.wrangler/`, `relay/dist/`, or any
  `packages.lock.json`. Lock files are RID/host-specific and are intentionally
  regenerated during restore.
- Do not add raw artwork under `assets/raw/`; only the tracked `.gitkeep` is
  allowed. Every non-fallback asset/provenance manifest must keep
  `privateUseOnly: true`.
- Keep the working tree clean after verification. Do not reset or discard user
  changes that are unrelated to the task.

## Toolchain

The supported release toolchain is:

- Windows 11 24H2/build 26100 or newer, x64.
- .NET SDK `10.0.112` as pinned by `global.json`.
- Visual Studio 2026 with the WinUI application development workload.
- PowerShell 7.4 or newer (`pwsh`).
- Node.js 24.21.0 and npm.
- Inno Setup 7.1.0 at `%ProgramFiles%\Inno Setup 7\ISCC.exe`.

The authoritative detailed runbook is [docs/release.md](docs/release.md).

## Normal development commands

Run from the repository root unless noted:

```powershell
dotnet restore DuduDesktop.slnx
dotnet build DuduDesktop.slnx -c Release --no-restore
dotnet test --solution DuduDesktop.slnx -c Release --no-build
```

Relay checks run from `relay/`:

```powershell
npm ci
npm run typecheck
npm test -- --run
npm run test:e2e
```

For local relay development:

```powershell
npm exec wrangler d1 migrations apply DB --local
npm run dev
```

Do not use a globally installed Wrangler when the repository dependency is
available. Use `npm exec wrangler` or `npx wrangler`.

## Building the Windows EXE/installer

### Recommended release build

On a clean, supported Windows machine, run the complete release gate from the
repository root:

```powershell
pwsh scripts/verify.ps1
```

This restores, builds, tests, validates the relay, publishes the self-contained
`win-x64` desktop app, compiles the private Inno Setup installer, runs installer
smoke/performance/end-to-end checks, writes the SHA-256 manifest, and checks
private-use and clean-tree invariants.

The final installer is:

```text
artifacts/DuduDesktop-1.0.0-win-x64-private.exe
```

Its checksum manifest is:

```text
artifacts/SHA256SUMS.txt
```

### Focused EXE/installer build

Use this when the full release gate has already been run or when iterating on
the Windows package:

```powershell
dotnet restore DuduDesktop.slnx
pwsh scripts/publish-windows.ps1 -Version 1.0.0
pwsh tests/installer/installer-smoke.ps1 `
  -Installer artifacts/DuduDesktop-1.0.0-win-x64-private.exe
```

The publish script performs a self-contained `win-x64` publish of
`src/Dudu.App/Dudu.App.csproj`, places the publish tree under
`artifacts/publish/win-x64`, and invokes Inno Setup. It removes only that
resolved publish directory after verifying it is beneath `artifacts/`.

After a successful build, generate or refresh the checksum in PowerShell:

```powershell
$hash = (Get-FileHash `
  artifacts/DuduDesktop-1.0.0-win-x64-private.exe `
  -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  DuduDesktop-1.0.0-win-x64-private.exe" |
  Set-Content -NoNewline artifacts/SHA256SUMS.txt
Get-Content artifacts/SHA256SUMS.txt
```

Before sending the EXE, compare the recipient-facing checksum against a fresh
`Get-FileHash` result. The installer is unsigned, so SmartScreen warnings are
expected; checksum verification is the release check.

### CI build and retrieving the latest artifact

Every push to `main` runs `.github/workflows/windows-installer.yml`. The
workflow is the authoritative Windows build because WinUI cannot be fully
published or exercised on macOS. It runs the Windows test suites, publishes
the app, runs the installer smoke test, calculates the checksum, and uploads
the artifact named `DuduDesktop-1.0.0-win-x64-private`.

```powershell
gh run list --workflow windows-installer.yml --limit 5
gh run watch <run-id> --exit-status
gh run download <run-id> `
  --name DuduDesktop-1.0.0-win-x64-private `
  --dir release-download
```

Only promote the downloaded EXE after the published checksum matches the
computed checksum. For a single retained local release, keep only:

```text
artifacts/DuduDesktop-1.0.0-win-x64-private.exe
artifacts/SHA256SUMS.txt
```

Do not commit or publicly upload either file.

### Production Microsoft Store MSIX procedure

The production Store path is separate from the ordinary acceptance-only Store
workflow. Use `.github/workflows/windows-store-production.yml` and run it only
from `main`; it is manual-only and uses the private repository's hosted
Windows x64 Actions minutes. The hosted runner is authoritative for this
package because macOS cannot run the real WinUI packaging and WACK path.

Before the first run, add these repository-level Actions secrets from the
Partner Center Product Identity page. Do not put the values in source, logs,
workflow inputs, or chat:

```text
DUDU_STORE_PARTNER_CENTER_NAME
DUDU_STORE_PARTNER_CENTER_PUBLISHER
DUDU_STORE_PARTNER_CENTER_PUBLISHER_DISPLAY_NAME
```

Use repository-level secrets. Environment-scoped copies with the older
`DUDU_PARTNER_CENTER_*` names can shadow them and appear empty on GitHub Free
private-repository runs. The workflow keeps the
`microsoft-store-production` environment as a future protection boundary but
does not depend on environment-scoped copies.

Trigger and watch the production package from macOS:

```zsh
cd "$DUDU_REPO"
gh workflow run windows-store-production.yml --ref main -f store_version=1.0.0
gh run list --workflow windows-store-production.yml --limit 1 \
  --json databaseId,headSha,status,conclusion,url
gh run watch <run-id> --interval 10 --exit-status
```

Do not treat the run as releasable unless it is successful and the job shows
successful identity injection, package build, WACK execution, Store-readiness
validation, checksum generation, and artifact upload. The production artifact
is named:

```text
DuduDesktop-1.0.0-win-x64-production-store
```

The workflow produces a single private `.msix` plus metadata. Verify the
artifact exists before using it:

```zsh
gh api "repos/$(gh repo view --json nameWithOwner --jq .nameWithOwner)/actions/runs/<run-id>/artifacts" \
  --jq '.artifacts[] | {name,size_in_bytes,expired}'
```

The package wrapper requires the Partner Center identity in the ephemeral
runner checkout and never commits it. It runs WACK and accepts a production
package when no required WACK test fails. The Windows Desktop Bridge guidance
classifies tests such as General Metadata correctness and Blocked executables
as optional/informational; their warnings or failures must remain in
`store-package-metadata/appcert-report.xml` and must not be silently removed.
An overall WACK `WARNING` caused only by optional tests is therefore expected
and is not a reason to discard the package. The workflow metadata must contain
`Windows App Certification Kit report: Store-ready`. Reference:
<https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-desktop-bridge-app-tests>.

The native app manifest must keep PerMonitorV2 DPI awareness enabled because
that is a required-quality check even though the hosted WACK report may still
show optional runtime warnings. If the production job fails, it uploads a
short-lived private diagnostic artifact named
`DuduDesktop-<version>-win-x64-store-diagnostics`; inspect its
`validation-summary.txt` and `appcert-report.xml` before changing the gate.

After a successful run, download the production artifact into a temporary
directory and inspect its metadata. Do not download the ordinary CI Store
artifact and do not upload the diagnostic artifact:

```zsh
export DUDU_STORE_RELEASE_TMP="$(mktemp -d /tmp/dudu-store-release-XXXXXX)"
gh run download <run-id> \
  --name DuduDesktop-1.0.0-win-x64-production-store \
  --dir "$DUDU_STORE_RELEASE_TMP"
find "$DUDU_STORE_RELEASE_TMP" -maxdepth 3 -type f -print
grep -F "Windows App Certification Kit report: Store-ready" \
  "$DUDU_STORE_RELEASE_TMP/store-package-metadata/validation-summary.txt"
cat "$DUDU_STORE_RELEASE_TMP/store-package-metadata/SHA256SUMS.txt"
```

Keep the `.msix` and checksum private. The final transfer is manual: open the
already-created private Partner Center submission, upload only the production
`.msix`, review the package identity and architecture, provide any restricted
capability justification, and submit for Microsoft's official certification.
Do not automate or silently perform that external file upload. The Store signs
the package during publication; local WACK is pre-submission evidence, not
Store approval.

### Exact Mac rebuild procedure

This is the procedure for rebuilding from the macOS authoring host. It is
important to understand what “build” means on Mac:

- macOS can run the relay tests and compile Windows-targeted .NET sources in a
  packaging-disabled stub mode.
- macOS cannot run WinUI, Windows App SDK's real XAML compiler/runtime path,
  DPAPI, Inno Setup, the Windows installer, or the real Windows EXE.
- Therefore a Mac cannot locally produce a releasable installer. The releasable
  EXE must come from the green Windows GitHub Actions artifact.
- Do not run `scripts/verify.ps1`, `scripts/publish-windows.ps1`, or a plain
  `dotnet publish` on Mac and call the output a release build. Those commands
  either require Windows-only tooling or produce output that has not passed the
  Windows build/package gate.

#### 1. Start in the repository and select the vendored tools

Use the repository's toolchain under `work/`, not an arbitrary system .NET or
PowerShell installation:

```zsh
export DUDU_REPO="$(git rev-parse --show-toplevel)"
cd "$DUDU_REPO"
export DUDU_DOTNET="$DUDU_REPO/work/dotnet-sdk/dotnet"
export DUDU_PWSH="$DUDU_REPO/work/tools/pwsh/pwsh"

test -x "$DUDU_DOTNET" || { echo "missing vendored dotnet: $DUDU_DOTNET" >&2; exit 1; }
"$DUDU_DOTNET" --version
node --version
npm --version
```

The expected .NET version is `10.0.112`. If `work/dotnet-sdk/dotnet` is
missing, stop and install/restore the repository's documented toolchain; do
not silently substitute another SDK for a release investigation. The vendored
PowerShell path is optional for the checks below and is not a Windows EXE
builder.

#### 2. Run the Mac-safe relay checks

Install relay dependencies, typecheck, run the Vitest suite, and run the
browser end-to-end suite:

```zsh
cd "$DUDU_REPO/relay"
npm ci
npm run typecheck
DUDU_DOTNET="$DUDU_DOTNET" npm test -- --run
npm run test:e2e
cd "$DUDU_REPO"
```

The `DUDU_DOTNET` assignment is required because the relay's C# crypto
interop tests spawn `dotnet`. Without it, a Mac with no system `dotnet` fails
with `spawn dotnet ENOENT` even though the vendored SDK exists.

#### 3. Compile the Windows projects in Mac stub mode

First restore the solution with the vendored SDK:

```zsh
cd "$DUDU_REPO"
"$DUDU_DOTNET" restore DuduDesktop.slnx
```

Then use these exact flags for Windows-targeted projects. The final four
flags disable the Windows-only XAML/page discovery steps that cannot execute
on macOS; omitting them causes misleading XAML compiler failures.

```zsh
export DUDU_MAC_STUB_FLAGS=(
  -c Release
  --no-restore
  -p:RuntimeIdentifier=win-x64
  -p:PlatformTarget=x64
  -p:WindowsAppSDKSelfContained=false
  -p:WindowsPackageType=None
  -p:AppxGeneratePriEnabled=false
  -p:GenerateAppInstallerFile=false
  -p:AppxPackageSigningEnabled=false
  -p:EnableCoreMrtTooling=false
  -p:ExpandPriResources=false
  -p:EnableDefaultApplicationDefinition=false
  -p:EnableDefaultPageItems=false
)

"$DUDU_DOTNET" build src/Dudu.App/Dudu.App.csproj \
  "${DUDU_MAC_STUB_FLAGS[@]}"
"$DUDU_DOTNET" build tests/Dudu.App.Tests/Dudu.App.Tests.csproj \
  "${DUDU_MAC_STUB_FLAGS[@]}"
"$DUDU_DOTNET" build tests/Dudu.UiTests/Dudu.UiTests.csproj \
  "${DUDU_MAC_STUB_FLAGS[@]}"
"$DUDU_DOTNET" build tests/Dudu.WindowsHarness/Dudu.WindowsHarness.csproj \
  "${DUDU_MAC_STUB_FLAGS[@]}"
```

Also run the host-runnable Core tests and the Infrastructure xUnit executable:

```zsh
"$DUDU_DOTNET" build tests/Dudu.Infrastructure.Tests/Dudu.Infrastructure.Tests.csproj -c Release
"$DUDU_DOTNET" build tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj -c Release

"$DUDU_REPO/tests/Dudu.Infrastructure.Tests/bin/Release/net10.0-windows10.0.26100.0/Dudu.Infrastructure.Tests" \
  -noLogo -noColor -longRunning 60
"$DUDU_REPO/tests/Dudu.Core.Tests/bin/Release/net10.0/Dudu.Core.Tests" \
  -noLogo -noColor -longRunning 60
```

The generated xUnit executables are the reliable Mac fallback when
`dotnet test` reports zero tests for a Windows-targeted project under
Microsoft Testing Platform. A zero-test result is not a passing test result.
The Infrastructure run normally has a small expected skip count for DPAPI and
the optional live relay test; it must have zero failures.

If a RID-specific restore fails with `NETSDK1047`, restore that exact project
for `win-x64`, then keep the generated lock file out of the source tree:

```zsh
"$DUDU_DOTNET" restore tests/Dudu.App.Tests/Dudu.App.Tests.csproj -r win-x64
find "$DUDU_REPO" -name packages.lock.json -print
```

Move any generated `packages.lock.json` into the ignored
`work/generated-package-locks/` scratch area before continuing. Never commit
one. If Core/Infrastructure tests then fail with stale assembly or file-load
errors after the RID builds, remove only the exact `bin/` and `obj/` directories
for `src/Dudu.Core`, `src/Dudu.Infrastructure`, `tests/Dudu.Core.Tests`, and
`tests/Dudu.Infrastructure.Tests`, rebuild those projects, and rerun the direct
test executables.

#### 4. Trigger the real Windows rebuild from Mac

If the code is already on `main` and you only need a fresh installer, trigger
the workflow manually:

```zsh
cd "$DUDU_REPO"
gh workflow run windows-installer.yml --ref main
```

If code changed, inspect the diff, commit the intended files, and push the
commit to `main`; the push triggers the same workflow. Do not push generated
files or private assets. Then identify the new run and wait for both jobs:

```zsh
gh run list --workflow windows-installer.yml --limit 5 \
  --json databaseId,headSha,status,conclusion,url
gh run watch <run-id> --interval 10 --exit-status
```

Do not download an artifact from a queued, failed, or stale run. The run must
show successful `Windows test suite (first-ever run)` and `Publish + package
(win-x64)` jobs. The package job must include a successful installer smoke test
and checksum step.

#### 5. Download, verify, and promote exactly one EXE

Download into a temporary directory, compare the published checksum with a
fresh local hash, and only then replace the canonical ignored artifact:

```zsh
cd "$DUDU_REPO"
export DUDU_RELEASE_TMP="$(mktemp -d /tmp/dudu-release-XXXXXX)"
gh run download <run-id> \
  --name DuduDesktop-1.0.0-win-x64-private \
  --dir "$DUDU_RELEASE_TMP"

export DUDU_DOWNLOADED="$DUDU_RELEASE_TMP/DuduDesktop-1.0.0-win-x64-private"
export DUDU_EXE="$DUDU_DOWNLOADED/DuduDesktop-1.0.0-win-x64-private.exe"
export DUDU_PUBLISHED_HASH="$(awk '{print $1}' "$DUDU_DOWNLOADED/SHA256SUMS.txt")"
export DUDU_COMPUTED_HASH="$(shasum -a 256 "$DUDU_EXE" | awk '{print $1}')"
test "$DUDU_PUBLISHED_HASH" = "$DUDU_COMPUTED_HASH" || {
  echo "checksum mismatch; refusing to promote artifact" >&2
  exit 1
}

mkdir -p "$DUDU_REPO/artifacts"
mv "$DUDU_EXE" "$DUDU_REPO/artifacts/DuduDesktop-1.0.0-win-x64-private.exe"
mv "$DUDU_DOWNLOADED/SHA256SUMS.txt" "$DUDU_REPO/artifacts/SHA256SUMS.txt"
rmdir "$DUDU_DOWNLOADED" "$DUDU_RELEASE_TMP"

test "$(find "$DUDU_REPO/artifacts" -maxdepth 1 -type f -name '*.exe' | wc -l | tr -d ' ')" -eq 1
shasum -a 256 "$DUDU_REPO/artifacts/DuduDesktop-1.0.0-win-x64-private.exe"
git status --short
```

The final `git status --short` may show unrelated user changes, but the
artifact promotion itself must not add tracked files. The only retained
release files are the one EXE and `SHA256SUMS.txt`; all other downloaded
temporary files must be gone.

## Testing and platform limitations

- New behavior needs regression coverage in the closest Core, Infrastructure,
  App, or relay test project.
- On Windows/CI, run the full solution tests and the hung-test finder defined in
  `.github/workflows/windows-installer.yml`.
- On macOS, Windows-targeted test projects may build but `dotnet test` can
  report zero tests under Microsoft Testing Platform. If needed, run the
  generated xUnit executable directly after building the project; do not treat
  a zero-test result as a passing test suite.
- macOS cannot provide authoritative WinUI, DPAPI, installer, performance,
  interactive UI, or private-note Windows acceptance. Use Windows CI and the
  manual matrix in `docs/testing/windows-acceptance.md` for those checks.
- When a test is intentionally skipped, record why. Do not weaken assertions
  merely to make a platform-limited test pass.

## Relay and security guidance

- The relay stores routing metadata and ciphertext only; never log note text,
  tokens, pairing codes, private keys, or request/response bodies.
- Preserve envelope validation, origin checks, authentication, rate limits,
  retention, and acknowledgement idempotency when changing relay routes.
- Registration must leave a recoverable local state if secret persistence is
  interrupted. The device ID is the completion marker and is written only after
  the desktop token.
- Scheduled message status must survive until the message's delivery window;
  do not make queued status expire from creation time when delivery is delayed.
- Use the existing privacy tests and adversarial regression tests as part of any
  remote-sync change.

## Diagnostics and logging

Status note: the crash/sync/chrome logging described below is implemented but
not yet merged. It lives as uncommitted changes in three isolated worktrees,
all branched from `cc66b12`:
`.worktrees/logging-p0` (global crash handlers + file sink + crash guard),
`.worktrees/logging-p1` (relay/sync visibility), `.worktrees/logging-p2`
(chrome + data-layer visibility). Review and merge those worktrees before
treating this section as authoritative for `main`.

### Log files on disk (all under `%LocalAppData%\DuduDesktop\logs\`)

- `startup-failure.log` — `src/Dudu.App/Hosting/StartupFailureLogger.cs`.
  Redacted (URLs and token-shaped values replaced before persistence), 64 KiB
  trim-to-recent cap. Written for startup/self-test failures and now for every
  last-chance crash (P0): phases `self-test`, `bootstrap`, `os-version`,
  `db-init`, plus `unhandled-ui` (`Application.UnhandledException`),
  `unhandled-domain` (`AppDomain.UnhandledException`), `unobserved-task`
  (`TaskScheduler.UnobservedTaskException`, observed after persisting).
  Reporting entry point: `src/Dudu.App/Hosting/GlobalCrashReporting.cs`
  (phase constants `PhaseUnhandledUi/Domain/Task`); handlers are subscribed in
  `App()` and never throw. The UI handler does not set `Handled` — crash
  behavior is preserved, now with a record.
- `diagnostics.log` — `src/Dudu.App/Hosting/FileDiagnosticLogger.cs`
  (`FileDiagnosticLoggerProvider`, 128 KiB trim-to-recent cap, swallows its own
  I/O errors, same redaction rules as `StartupFailureLogger`). Wired via
  `AddFileDiagnosticLogging(paths)` in
  `WindowsCompanionProductionComposition`, so `AppHost.ResolveErrorReporter`
  now finds a file-backed `ILoggerFactory` instead of falling back to
  `Trace`+`Console.Error`. `Trace.*` remains as a secondary signal only.
- `startup-crash-count` (in the data root, not `logs/`) — consecutive failed
  runs for `StartupCrashGuard`; `BeginRun` now records the count and an
  explicit safe-mode entry to `diagnostics.log` + `Trace` (never to
  `startup-failure.log`). Counter format and `MarkCleanRun` semantics are
  unchanged.

### Logging surfaces and rules for new code

- Relay/remote-sync path: `src/Dudu.Infrastructure/Logging/PrivacySafeLog.cs`
  is the ONLY allowed surface. Callers must not call `ILogger` directly for
  anything touching the relay or an envelope. Event IDs: `1001` poll ok,
  `1002` relay failed (status + category), `1003` envelope rejected
  (message ID + reason), `1004` self-test step failed, `1005`
  `SyncStateProbeFailed` (on-demand `GetStateAsync` transient failures —
  previously swallowed silently), `1006` `SyncLoopTerminal`
  (`protocol-backoff` / `needs-repair` / drain faults), `1007` `SyncLoopRetry`
  (logged in ADDITION to the `_reportError` callback, never instead of it),
  `1008` `RelayStagingCleanupFailed`, `1009` `RelayStagingPromoted` (replaces
  the old generic 401 `promoted-staged-token` line). Every method carries only
  counts, status codes, fixed tags, exception-type names, or envelope GUIDs.
- Chrome/data path: failures route through `IAppHostErrorReporter`
  (`AppHost.ErrorReporter`, shared with `AppLifecycleCoordinator`,
  overlay, tray, hotkey, event source, presentation and notification sinks)
  where one is composed, else legacy diagnostic, else a `Trace` line with
  operation + exception type/HResult only. Operation names in use:
  `hotkey-attach`, `hotkey-set-gesture`, `tray-attach`, `tray-recreate`,
  `taskbar-tray-recreate`, `overlay-create`, `overlay-dispose`,
  `overlay-message-loop`, `fullscreen-poll` (fail-closed to hidden, unchanged),
  native callbacks (`session-lock/unlock`, `suspend`, `resume`,
  `display-change`, `taskbar-created`, `hotkey`), `remote-note-notify`,
  `reminder-notify`, `presentation-tick`, `toast-notify`,
  `startup-chrome-attach`, `partial-startup-*` / `partial-runtime-*` /
  `runtime-*-shutdown` cleanup ops, `shutdown-host`, `shutdown-overlay`,
  `shutdown-tray`. All best-effort/fail-closed behavior is unchanged —
  logging only, report-and-(re)throw where the original threw.
- Data-layer failure phases recorded via `StartupFailureLogger`
  (report-and-(re)throw, ordering guarantees untouched):
  `Database.InitializationFailurePhase = "db-init"`,
  `DpapiSecretStore` `ReadFailurePhase = "secret-read"` /
  `WriteFailurePhase = "secret-write"` (absent reads, idempotent deletes,
  cancellations, and validation never report),
  `DatabaseBackupService.PruneFailurePhase = "backup-prune"`
  (best-effort prune, still swallowed) and the same phase on
  `LocalDataMaintenanceService` sweep failure (still propagates).
- Never log note plaintext, tokens, pairing codes, keys, ciphertext, URLs, or
  request/response bodies. `DesktopKeyService` must never take an `ILogger`
  (enforced by a structural test in `PrivacyBoundaryTests`). `Trace`
  fallbacks carry type+HResult only, never message/stack; full exceptions go
  to the reporter sink only.
- Relay worker (`relay/`): keep the no-PII rule — per-route category counters
  only (`route`, `method`, `status code`, error class), never bodies, headers,
  tokens, or envelope fields. Not yet implemented; `router.ts` still logs one
  generic line.

### How to diagnose errors

Symptom-first lookup (data root overridable via `DUDU_DATA_ROOT`):

- App never opens / exits immediately: read `logs\startup-failure.log`
  (newest entry first — check `phase=`); cross-check `startup-crash-count`
  and `diagnostics.log` safe-mode lines for 3+ consecutive failed runs.
- Pet disappears, tray icon or hotkey stops working: search `diagnostics.log`
  for `tray-attach`, `tray-recreate`, `hotkey-attach`, `overlay-create`,
  `fullscreen-poll`, `session-lock`, `suspend`, `display-change`.
- Notes stop arriving / Connection page stale: search for `1005`/`1006`/`1007`
  (probe failures, terminal loop state, retries) and `remote-sync-protocol`
  reports; `1009` confirms a staged-token promotion after an interrupted
  key rotation; repeated `1003` with reason `oversize` means poison envelopes
  are being acked-and-skipped by design.
- Unopened-note or settings data loss after crash: check for `db-init`,
  `secret-read`, `secret-write`, `backup-prune` phases; secret-write ordering
  (token before device ID) is a recovery invariant — do not "fix" it.
- Relay-side 5xx spike: `wrangler tail`, correlate `route` + status code +
  error class; never ask for or paste request bodies.
- Tests for any of the above: `FieldDiagnosticsContractTests`,
  `FileDiagnosticLoggerTests`, `GlobalCrashReportingTests`,
  `StartupCrashGuardDiagnosticsTests`, `ChromeDiagnosticsTests`,
  `SinkDiagnosticsTests`, `DataFailureDiagnosticsTests`,
  `DpapiSecretStoreDiagnosticsTests`, plus the extended `RemoteSyncServiceTests`
  / `RelayClientTests` leak assertions (follow the `Logs_never_include`
  marker pattern). `Dudu.App.Tests` cannot execute on macOS (WinUI) — it
  compiles there and runs on Windows CI.

## Release handoff

Before handing the installer to anyone:

1. Run `pwsh scripts/verify.ps1` on supported Windows, or confirm the matching
   Windows CI run is green.
2. Confirm `artifacts/SHA256SUMS.txt` matches the exact EXE being sent.
3. Confirm there is exactly one retained `.exe` in `artifacts/`.
4. Keep the repository, relay URL, sender URL, artwork, installer, and checksum
   in private channels only.
5. Tell the recipient that the installer is unsigned and that the checksum must
   be verified before bypassing SmartScreen.

For installation, upgrade, uninstall, pairing, backup/restore, and the full
acceptance matrix, follow [docs/release.md](docs/release.md) and
[docs/testing/windows-acceptance.md](docs/testing/windows-acceptance.md).

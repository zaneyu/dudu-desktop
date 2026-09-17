# Release runbook — Dudu Desktop Companion (private v1)

This is a private, single-recipient release. The artwork and installer must
remain private. Nothing in this document authorizes publishing the repository,
the Worker, the sender page, the installer, or this release tag.

## 1. Prerequisites

- Windows 11, version 24H2 (build 26100) or newer, x64.
- Visual Studio 2026 with the **WinUI application development** workload.
- .NET SDK 10.0.112 (`dotnet --version`).
- Node.js 24.21.0 and npm (`node --version`; `npm --version`). A newer Node
  major version will print an `EBADENGINE` warning from npm — that is
  advisory only and does not block `npm ci`.
- Inno Setup 7 installed at its default location
  (`%ProgramFiles%\Inno Setup 7\ISCC.exe`) — `scripts/publish-windows.ps1`
  throws a clear error if it is missing.
- CI downloads Inno Setup 7.1.0 from the pinned GitHub release in
  `installer/inno-setup-pinned.json` and verifies its SHA-256 digest before
  starting the installer. Update the URL, filename, version, and digest
  together when intentionally upgrading Inno Setup; never remove that check.
- PowerShell **7.4 or newer** (`pwsh --version`) for every script in this
  runbook and in `scripts/verify.ps1`. `verify.ps1` declares
  `#Requires -Version 7.4` and relies on
  `$PSNativeCommandUseErrorActionPreference` (stable by default only from
  7.4 onward) so a failing `dotnet`/`node`/`npm`/nested-`pwsh` call stops the
  script immediately instead of silently continuing.
- A Cloudflare account with Workers and D1 enabled, and the `wrangler` CLI
  (installed as a `relay/` dev dependency — invoke it as `npm exec wrangler`
  or `npx wrangler`, not a separately installed global).

## 2. Relay: local development

From `relay/`:

```powershell
npm ci
npm exec wrangler d1 migrations apply DB --local
npm run dev            # or: npm exec wrangler dev
```

`wrangler.jsonc` binds the D1 database as `DB` and reads migrations from
`relay/migrations/` (`0001_identity.sql`, `0002_messages.sql`, and
`0003_pairing_winner.sql`, applied in order). The local D1 database file lives
under `.wrangler/` and is already git-ignored.

## 3. Relay: production Cloudflare setup

1. Create the production D1 database:

   ```powershell
   npm exec wrangler d1 create dudu-relay
   ```

   The command prints a `database_id`. Open `relay/wrangler.jsonc` and
   set `database_id` under the `DB` binding to that UUID (the checked-in
   file already carries the production database for this deployment).
   Leave `database_name` as `dudu-relay` (or whatever name you created)
   and `migrations_dir` as `"migrations"`.

   **Do this before deploying.** Deploying with the placeholder ID still
   unset fails the upload with Cloudflare API error 10181 ("D1 binding 'DB'
   references database '00000000-0000-0000-0000-000000000000' which was not
   found") — verified against a real account while writing this runbook.
   Static assets upload before that check runs, so a failed deploy for this
   reason leaves no live Worker version; it is safe to fix the ID and deploy
   again.

2. Apply migrations to the production database:

   ```powershell
   npm exec wrangler d1 migrations apply DB --remote
   ```

3. Create the `PAIRING_CODE_PEPPER` secret (used to hash one-time pairing
   codes at rest — never logged or returned by any endpoint):

   ```powershell
   npm exec wrangler secret put PAIRING_CODE_PEPPER
   ```

   Paste a long random value when prompted; wrangler does not echo it back.

4. Deploy:

   ```powershell
   npm exec wrangler deploy
   ```

   **Warning: do not try to "dry-run" or probe this with `-- --help`.**
   Unlike `d1 create`, `d1 migrations apply`, and `secret put` — which all
   take a mandatory positional argument and print help instead of running
   when you append `-- --help` without one — `deploy` and `rollback` take no
   required positional argument, so `npm exec wrangler deploy -- --help` (or
   `rollback -- --help`) does **not** show help: it executes the real
   command for real against whichever Cloudflare account `wrangler` is
   currently authenticated as. This was confirmed the hard way while writing
   this runbook (see this task's report for the full incident writeup); the
   static-asset upload step runs before the D1 binding is validated, so a
   deploy that fails on the placeholder `database_id` (§3 step 1) still
   uploads assets first, even though it never publishes a live Worker
   version.

5. Roll back to the previous stable Worker version if a deploy misbehaves:

   ```powershell
   npm exec wrangler rollback
   ```

   `rollback` without a version ID targets the most recent stable version;
   pass an explicit version ID (from `npm exec wrangler deployments list`) to
   roll back further. On a Worker with no prior deployment, `rollback` fails
   with "Could not find stable Worker Version to rollback to" — expected
   before the first successful deploy, not an error to chase.

Record the deployed Worker URL somewhere private (a password manager entry,
not source control, not this file, not any public note) — it, like the rest
of this release, must remain private.

## 4. Desktop: build, checksum, install, upgrade, uninstall

All commands run from the repository root with `pwsh`.

1. Build, publish, package, and verify in one step:

   ```powershell
   pwsh scripts/verify.ps1
   ```

   This runs the full contract described in that script's header comment,
   including `pwsh scripts/publish-windows.ps1 -Version 1.0.0`, the isolated
   installer smoke test, the performance gates, and the private-note
   end-to-end flow, and writes `artifacts/SHA256SUMS.txt`.

2. Checksum: `artifacts/SHA256SUMS.txt` contains one
   `<sha256 hex>  <filename>` line (two spaces, lowercase hex — the same
   format `sha256sum` produces) per released file, generated from
   `Get-FileHash -Algorithm SHA256`. Before sending the installer to the
   recipient's machine, recompute and compare:

   ```powershell
   Get-FileHash -Algorithm SHA256 artifacts\DuduDesktop-1.0.0-win-x64-private.exe
   ```

   **For a CI-built installer, `SHA256SUMS.txt` alone proves nothing** — it
   travels inside the same artifact as the EXE, so anyone who could swap the
   EXE could swap the file too. The recipient-facing hash must instead be
   compared against the value in the **run summary** of the
   `windows-installer.yml` run that produced it:

   ```powershell
   gh run view <run-id>          # or open the run page; see the
                                 # "Verify uploaded installer + publish checksum" job summary
   (Get-FileHash -Algorithm SHA256 release-download\DuduDesktop-1.0.0-win-x64-private.exe).Hash.ToLowerInvariant()
   ```

   That summary is written by a separate `verify-artifact` job on a fresh
   runner, which downloads the stored artifact, recomputes its SHA-256, and
   fails the run unless it equals both the build job's hash and
   `SHA256SUMS.txt`. The summary outlives the artifact's 30-day retention.
   Limits, stated plainly: the hash is still *computed* by the build job on
   the same runner that built the binary, and the run summary is only as
   trustworthy as write access to this repository — this is integrity of
   transfer, not independent provenance.

   **Build provenance (`gh attestation verify`) is not currently produced.**
   GitHub issues artifact attestations for private repositories only on
   GitHub Enterprise Cloud; this is a personal private repository, so the
   `actions/attest-build-provenance` step is present but gated behind the
   repository variable `DUDU_ENABLE_ATTESTATION=true`. Only set it after
   moving the repository to an Enterprise Cloud organization (attestations
   then go to GitHub's private Sigstore instance). Once enabled, verify with:

   ```powershell
   gh attestation verify release-download\DuduDesktop-1.0.0-win-x64-private.exe --repo <owner>/dudu-desktop
   ```

   Do **not** make the repository public to obtain attestations: public
   attestations are recorded in the public Sigstore transparency log, which
   would publish the installer hash and repository identity.

   **Authenticode signing is not done.** There is no code-signing
   certificate for this private build, so the EXE carries no publisher
   signature; the checksum comparison above is the release check.

   **What is bundled.** `installer/DuduDesktop.iss` uses recursive wildcards
   over `artifacts/publish/win-x64` and the private pack. An exact per-file
   list of the ~530-file self-contained WinUI tree is impractical (names
   change with every Windows App SDK/.NET servicing bump), so
   `scripts/publish-windows.ps1` refuses to run ISCC unless every published
   file matches `$PublishTreeAllowlist` (named executables only, root
   binaries/metadata/resources, culture `.mui` folders, the XAML and app
   asset folders, and SkiaSharp's one native PDB) and the private pack holds
   only `manifest.json` plus `frames/**/*.png`. A new legitimate file shape
   fails the build naming the file; extend the allowlist and
   `tests/scripts/publish-manifest.tests.ps1` together.

   **Release metadata.** Each CI run also uploads
   `DuduDesktop-1.0.0-release-metadata` (90-day retention) from
   `artifacts/release-metadata/`: `publish-manifest.txt` (SHA-256 of every
   bundled publish file), `dotnet-info.txt` (`dotnet --info`),
   `dotnet-packages.txt` (`dotnet list package --include-transitive`), and
   `lockfiles/**` (the `packages.lock.json` files generated by that run's
   restore). Treat it as private like the installer.

3. Install: run `artifacts\DuduDesktop-1.0.0-win-x64-private.exe`. It
   installs per-user (no elevation) under
   `%LocalAppData%\Programs\DuduDesktop`, with Start Menu and optional
   desktop shortcuts. **SmartScreen may warn because the private installer
   is unsigned.** This is expected for a private, unsigned build — verify
   the checksum above instead of relying on SmartScreen, and proceed past
   the warning only after the checksum matches.

4. Upgrade: run a newer installer build the same way; Inno Setup's
   `CloseApplications=yes` closes a running Dudu Desktop belonging to that
   installation, then overwrites the previous install in place under the same
   app ID. The installer does not image-wide force-kill Dudu processes. The
   launch-at-sign-in shortcut (if enabled) is owned by the app itself, not
   the installer, and survives an upgrade unchanged.

5. Uninstall: use Windows Settings → Apps, or run the uninstaller under
   `%LocalAppData%\Programs\DuduDesktop`. Uninstalling removes the
   application files and shortcuts; it does not delete the local database,
   backups, or secrets under `%LocalAppData%\DuduDesktop` unless the
   recipient separately chooses "delete local data" from the app's Privacy
   & Data page first (see §7). This matches the final acceptance checklist's
   "optional data deletion" requirement — deletion is optional and
   recipient-initiated, not automatic on uninstall.

## 5. Private Store submission and migration fallback

The Store path is a private-audience distribution path, not a public release.
Reserve and maintain the private-audience Store app in Partner Center, and
keep its audience restricted to the invited recipient account(s). The local
non-production package identity is for development and acceptance only; it is
not Store-publishable. **Fail closed: never upload the CI package made with
`DuduDesktop.Local.NonProduction` to Partner Center.**

The owner must first configure the exact Partner Center `Identity Name` and
`Publisher` in the private working copy of `src/Dudu.App/Package.appxmanifest`.
Then the owner must produce the submission package with the identity gate:

```powershell
pwsh scripts/package-store.ps1 -Version <Major.Minor.Patch> `
  -RequirePartnerCenterIdentity `
  -ExpectedPartnerCenterName '<exact Partner Center Identity Name>' `
  -ExpectedPartnerCenterPublisher '<exact Partner Center Publisher>'
```

The command fails unless the manifest matches both exact values and is not the
local identity. Only the package produced after that successful gate may be
submitted; the ordinary CI artifact remains acceptance-only. Do not commit the
private identity values. Store artifacts become Microsoft-signed only after
Microsoft Store publication. The current Inno installer remains unsigned and
may trigger SmartScreen or Smart App Control behavior.

For each Store release:

1. Wait for a green Windows workflow, including its package and verification
   jobs. A CI artifact is not evidence that recipient Windows acceptance has
   completed. The CI Store artifact is acceptance-only: **never upload it to
   Partner Center**.
2. Download the workflow artifact named
   `DuduDesktop-<version>-win-x64-store` to a newly created temporary directory.
   It contains the `.msix` package under `artifacts/store-package/` and
   the private release metadata under `artifacts/store-package-metadata/`.
3. Use that CI `.msix` only for acceptance and integrity verification. Compare
   its SHA-256 hash with the `Store package SHA-256` value in the Store job
   summary and with `store-package-metadata/SHA256SUMS.txt`; also confirm
   `package-version.txt` matches the intended release. This comparison applies
   only to the acceptance artifact; it is not an upload authorization.
4. Separately configure the exact Partner Center `Identity Name` and
   `Publisher` in the private working copy of
   `src/Dudu.App/Package.appxmanifest`, then run the identity-gated local
   command above. It must produce a new local `.msix` and its own metadata.
5. Verify the locally produced `.msix` against its own
   `store-package-metadata/SHA256SUMS.txt` and `package-version.txt`, confirming
   the intended strictly increasing version. Do not substitute the CI package
   or use its job-summary hash for this local package.
6. Upload only that locally produced, identity-gated `.msix` (the Partner
   Center upload container, if the portal requests one, is not the raw `.msix`)
   to Partner Center, keep the audience private, and submit it for
   certification.
7. After publication, verify from the recipient's invited Store account that
   the private Store listing is visible and that Dudu Desktop installs.
8. For later updates, start the workflow with `workflow_dispatch` and enter
   the explicit `store_version` three-part value. Pushes retain the `1.0.0`
   default for repeatable acceptance builds. Use a strictly increasing package
   version and confirm the corresponding package filename and
   `package-version.txt` before submission. If a rollout is bad, pause it in
   Partner Center and submit a corrected package with the next increasing
   version.

During migration, retain the Inno installer as the recovery path until two
Store versions have upgraded successfully on the recipient's Windows device.
A package identity change can affect startup shortcuts, notifications,
activation, and uninstall behavior even when `%LocalAppData%\DuduDesktop` is
preserved. Complete the Windows acceptance matrix in
`docs/testing/windows-acceptance.md` before calling the Store path the normal
recipient release.

### Recipient procedure for the private Store path

1. Sign in to Microsoft Store with the invited personal account.
2. Open the private Store link supplied through the private release channel.
3. Install Dudu Desktop and leave Store app updates enabled.
4. If removing an old Inno installation, do not delete local data unless you
   intentionally want to wipe it from Dudu's Privacy & Data page.

## 6. First pairing and sender revocation

1. On the desktop, open Settings → Connection and request a pairing code.
   This calls the relay's `POST /v1/devices/pairing-code` endpoint,
   which returns a short one-time code that expires after ten minutes.
2. On the recipient's phone, open the private sender page URL (kept private
   per §3) and enter the code. The browser calls
   `POST /v1/pairings/redeem`; on success it receives a secure, HttpOnly
   session cookie and the sender composer appears.
3. To revoke a paired sender (lost phone, ended pairing, etc.), use the
   desktop's Connection page "disconnect" action, which calls
   `POST /v1/sender/disconnect`. The recipient can re-pair at any time by
   generating a new code.

## 7. Database backup, restore, and damaged-database preservation

The local SQLite database lives at `%LocalAppData%\DuduDesktop\dudu.db`
(override the whole data root with the `DUDU_DATA_ROOT` environment
variable). `DatabaseBackupService`
(`src/Dudu.Infrastructure/Data/DatabaseBackupService.cs`) backs the database
up using SQLite's `VACUUM INTO`, validates every backup with
`PRAGMA integrity_check` before trusting it, and keeps the newest
`BackupRetentionCount` (default 5) valid backups under
`%LocalAppData%\DuduDesktop\backups\`, named
`{yyyyMMddHHmmssfff}-{guid}.db`.

- **Automatic backup before migration**: `MigrationRunner`
  (`src/Dudu.Infrastructure/Data/MigrationRunner.cs`) always creates a
  pre-migration backup before applying any pending schema migration, so a
  failed or interrupted migration never leaves the recipient without a
  recoverable prior state.
- **Manual backup/restore**: the app's Privacy & Data page
  (`src/Dudu.App/Pages/PrivacyDataPage.xaml`,
  `src/Dudu.App/ViewModels/PrivacyDataViewModel.cs`) exposes "Backup now" and
  "Restore latest backup" actions that call the same backup service.
- **Damaged-database preservation**: if a restore attempt fails with an
  `IOException` or `UnauthorizedAccessException` partway through, the
  in-progress (possibly damaged) database file is renamed to
  `dudu.db.restore-failed-{guid}` instead of being deleted, so a human can
  recover data from it later rather than silently losing it.

## 8. Private-use statements

- SmartScreen may warn because the private installer is unsigned.
- The artwork and installer must remain private.

Per the final acceptance checklist: the repository, artwork, Worker URL,
sender page, installer, and this release tag must all remain private. Do not
push this tag, this branch, or the installer to any public location. Transfer
the installer and `artifacts/SHA256SUMS.txt` to the recipient only through a
private channel.

## 9. Known deviations and pending items

- **`dotnet restore --locked-mode` is intentionally omitted.** `scripts/verify.ps1`
  runs `dotnet restore DuduDesktop.slnx` without `--locked-mode`. This
  repository's projects all set `RestorePackagesWithLockFile=true`
  (`Directory.Build.props`), so every restore regenerates a
  `packages.lock.json` per project — but that file is RID-specific and
  differs between the macOS authoring host and the Windows release host, so
  **no `packages.lock.json` is ever committed**. Host-local snapshots live
  under `work/generated-package-locks/`, which — like all of `work/` and
  `outputs/` — is ignored by `.gitignore` and never tracked;
  `Assert-NoTrackedGeneratedArtifacts` in `scripts/verify.ps1` fails the
  release if any lock file, or anything under `work/` or `outputs/`, turns
  up in the git index. `--locked-mode` would require the checked-in lock
  file to exactly match the resolved graph on whichever host runs it, which
  this per-host-regeneration setup cannot satisfy. What stands in for a
  committed lock: `global.json` pins SDK `10.0.112` with
  `rollForward: "disable"` (CI's `setup-dotnet` installs exactly that
  version from `global.json`, and the host refuses any other SDK, including
  a newer preinstalled patch — install exactly 10.0.112 locally too);
  `Directory.Packages.props` uses only exact package versions (no floating
  or range versions; `tests/scripts/release-contract.tests.ps1` enforces
  this); and every release build archives its generated lock files and
  resolved transitive package list in the release-metadata artifact (§4).
  Residual gap: NuGet does not fail a build whose graph differs from a
  previous release's; diff the archived `lockfiles/` between runs when that
  matters.
- **The relay URL is baked in.** `Dudu.Core.ProductInfo.DefaultRelayBaseUrl`
  (`src/Dudu.Core/ProductInfo.cs`) carries the deployed private Worker URL
  for the v1 release, so an installed copy needs no configuration. The
  URL itself is private: keep it out of public repositories and share it
  only with the recipient. `DUDU_RELAY_BASE_URL` remains an **override**
  (`src/Dudu.App/Hosting/RelayConfiguration.cs`): it is read at startup
  and must parse as an absolute `http`/`https` URI; an unset or invalid
  value falls back to the baked-in default. Only if both are missing does
  the app fall back to `OfflinePairingService` with no relay activated.
- **First Windows run: partially done, on CI only.** This release was
  authored on a macOS host using a documented stub-build path. GitHub
  Actions run 34815474329 (`.github/workflows/windows-installer.yml`,
  `windows-latest` = Windows Server 2025 x64, 2026-09-14) has since executed
  the WinUI publish, Inno Setup packaging, the installer smoke test
  (including `--self-test`) and every `dotnet test` project — Core,
  Infrastructure and, for the first time, `Dudu.App.Tests` — all green. The
  FlaUI UI tests, the performance gates against a real executable, the
  harness scenarios and the private-note end-to-end flow have **still not
  been executed**: they need an interactive Windows desktop session, which
  a hosted runner does not provide. `docs/testing/windows-acceptance.md`
  tracks every row that still needs a real Windows 11 24H2 x64 run before
  this release can be considered fully verified.

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

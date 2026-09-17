# Private Store MSIX Distribution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a private Microsoft Store MSIX distribution path for Dudu Desktop so the recipient can install and receive future updates through the Store without manually receiving unsigned installers.

**Architecture:** Keep the existing unpackaged WinUI + Inno Setup path as the fallback and development path during migration. Add conditional single-project MSIX packaging to `Dudu.App`, produce a Store-uploadable x64 package in Windows CI, and preserve the existing `%LocalAppData%\\DuduDesktop` data contract. Use Partner Center's private-audience Store distribution and Store-managed updates; do not add a custom desktop updater or installer-download endpoint.

**Tech Stack:** .NET SDK 10.0.112, WinUI 3, Windows App SDK 2.3.1, MSIX, Microsoft Store Partner Center, GitHub Actions, PowerShell 7.4+, xUnit v3, Windows App Certification Kit.

**Spec:** `docs/superpowers/specs/2026-09-17-private-store-msix-distribution-design.md`

## Global Constraints

- Supported release host: Windows 11 24H2/build 26100 or newer, x64.
- Supported SDK: .NET SDK `10.0.112` with `rollForward: "disable"`.
- Preserve dependency direction: `Dudu.App` → `Dudu.Infrastructure` → `Dudu.Core`.
- Preserve the default data root: `%LocalAppData%\\DuduDesktop`.
- Keep relay URL, sender URL, artwork, installer, Store identity values, and release metadata private.
- Never commit `bin/`, `obj/`, `artifacts/`, `work/`, `outputs/`, `node_modules/`, `.wrangler/`, `relay/dist/`, or `packages.lock.json`.
- Every non-fallback asset/provenance manifest keeps `privateUseOnly: true`.
- Do not stage, reset, discard, or rewrite the existing uncommitted audit-fix changes.
- The normal recipient update path is Store-managed; no custom updater executes downloaded installers.
- A releasable result requires real Windows validation; macOS builds are only Mac-safe compatibility checks.

---

## File and component map

### Existing files to modify

- `src/Dudu.App/Dudu.App.csproj` — add a Windows-only, opt-in MSIX packaging property group while keeping unpackaged builds as the default.
- `DuduDesktop.slnx` — include the packaging project only if the single-project MSIX build probe proves the current SDK cannot package the app in-place; do not add a second packaging project preemptively.
- `.github/workflows/windows-installer.yml` — add a Windows package job/artifact and retain the current Inno release job.
- `scripts/verify.ps1` — keep the current Inno gate intact and add Store package contract checks only where they are cross-platform and non-destructive.
- `README.md` — document the two release paths and identify the Store path as the recipient path after acceptance.
- `docs/release.md` — document Store identity setup, private audience setup, package upload, Store submission, and the migration/recovery boundary.
- `docs/testing/windows-acceptance.md` — add the Store install, upgrade, Smart App Control, and data-preservation rows.
- `tests/scripts/release-contract.tests.ps1` — add source-level assertions for the Store packaging contract and private-data exclusions.

### New files

- `src/Dudu.App/Package.appxmanifest` — package identity, full-trust Win32 application declaration, visual elements, and supported Windows target.
- `src/Dudu.App/PackageAssets/Logo.png` and required scale-qualified logo assets — package identity visuals derived from the existing private/fallback Dudu asset without adding anything under `assets/raw/`.
- `scripts/package-store.ps1` — Windows-only package build and validation wrapper that emits a Store-uploadable artifact under ignored `artifacts/`.
- `tests/scripts/store-package.tests.ps1` — cross-platform XML/source contract checks that do not require MSIX tooling.
- `tests/Dudu.App.Tests/Hosting/PackagedDataPathTests.cs` — regression coverage proving packaging does not alter the default durable data root.
- `tests/Dudu.App.Tests/System/PackagedStartupRegistrationTests.cs` — regression coverage for startup registration behavior under package identity where the existing seams permit it.
- `docs/superpowers/plans/2026-09-17-private-store-msix-distribution.md` — this implementation plan.

### External configuration, not committed to the repository

- Partner Center app reservation and private audience membership.
- GitHub Actions secrets/variables for the Store submission account and the exact reserved package identity.
- Recipient's personal Microsoft account email for the private audience.

---

### Task 1: Prepare the private Store identity

**Files:**
- Modify: none; Partner Center and GitHub repository settings only.

**Interfaces:**
- Produces the exact package name, publisher identity, display name, and private-audience recipient account needed by later packaging tasks.

- [ ] **Step 1: Create or verify the individual Microsoft Store developer account**

  Start at `https://storedeveloper.microsoft.com`, select the individual developer flow, complete Microsoft's identity verification, and confirm that Partner Center can create an app submission.

- [ ] **Step 2: Reserve the Store app name and configure private visibility**

  Reserve `Dudu Desktop` or the available final display name. Configure the submission as a free app with a private audience containing only the girlfriend's personal Microsoft account. Do not choose a public audience first, because the Store does not allow changing a previously public submission back to private audience.

- [ ] **Step 3: Record package identity values privately**

  Record the exact `Identity Name`, `Publisher`, and `Display Name` values shown by Partner Center in the private release password manager and GitHub repository variables. The package name and publisher are identifiers rather than credentials, but keep them out of public documentation because the product and artwork are private.

- [ ] **Step 4: Verify the recipient account requirement**

  Confirm that the recipient will sign into Microsoft Store on Windows with the same personal Microsoft account added to the private audience. A work/school account is not a substitute for this private-audience membership.

- [ ] **Step 5: Commit no repository changes**

  Confirm with `git status --short` that the existing audit-fix changes are unchanged. Do not add Partner Center exports, access tokens, screenshots containing private URLs, or certificate files to the repository.

---

### Task 2: Add conditional single-project MSIX packaging

**Files:**
- Create: `src/Dudu.App/Package.appxmanifest`
- Create: `src/Dudu.App/PackageAssets/Logo.png` and scale-qualified variants
- Modify: `src/Dudu.App/Dudu.App.csproj`
- Test: `tests/scripts/store-package.tests.ps1`

**Interfaces:**
- Consumes: the exact Store identity values from Task 1 and the existing `Dudu.App` WinUI entry point.
- Produces: an opt-in `DuduStorePackage=true` build mode that emits a full-trust x64 MSIX without changing the default Mac stub or Inno build modes.

- [ ] **Step 1: Write the failing package contract test**

  Add assertions in `tests/scripts/store-package.tests.ps1` that load `src/Dudu.App/Dudu.App.csproj` and `src/Dudu.App/Package.appxmanifest`, then require:

  ```powershell
  Assert-True "app project keeps unpackaged mode as the default" (
      $appProject -match '<WindowsPackageType>None</WindowsPackageType>'
  )
  Assert-True "app project exposes an opt-in Store package property" (
      $appProject -match "DuduStorePackage.*true"
  )
  Assert-True "manifest declares x64 identity" ($manifest.Package.Identity.ProcessorArchitecture -eq "x64")
  Assert-True "manifest declares full-trust application" ($manifest.Package.Capabilities.'rescap:Capability'.Name -contains "runFullTrust")
  Assert-True "manifest declares a supported target device family" ($manifest.Package.Dependencies.TargetDeviceFamily.Name -eq "Windows.Desktop")
  ```

  Use namespace-aware XML access rather than string parsing for manifest identity and capabilities; keep source-level regex assertions only for the conditional MSBuild property.

- [ ] **Step 2: Run the contract test and verify it fails**

  Run from the repository root:

  ```powershell
  pwsh tests/scripts/store-package.tests.ps1
  ```

  Expected result: FAIL because `Package.appxmanifest` and the opt-in Store property do not yet exist.

- [ ] **Step 3: Add the opt-in MSIX property group**

  Keep the existing `<WindowsPackageType>None</WindowsPackageType>` as the default and append a Windows-only property group:

  ```xml
  <PropertyGroup Condition="'$(DuduStorePackage)' == 'true' and '$(OS)' == 'Windows_NT'">
    <WindowsPackageType>MSIX</WindowsPackageType>
    <EnableMsixTooling>true</EnableMsixTooling>
    <AppxPackageSigningEnabled>false</AppxPackageSigningEnabled>
    <AppxBundle>Never</AppxBundle>
    <GenerateAppInstallerFile>false</GenerateAppInstallerFile>
  </PropertyGroup>
  ```

  Do not set `DuduStorePackage=true` globally. The ordinary Mac stub, Core/Infrastructure tests, App tests, Windows harness, and Inno publish path must continue to use the existing unpackaged project settings.

- [ ] **Step 4: Add the package manifest**

  Create a valid full-trust desktop manifest with the Task 1 identity values, `ProcessorArchitecture="x64"`, the repository's Windows 11 minimum version, the existing app executable entry point, and visual elements that reference checked-in package assets. Include `runFullTrust`, but do not declare network, broad file-system, or other capabilities not already required by the app.

  The application declaration must use the Windows App SDK full-trust token form:

  ```xml
  <Application
      Id="App"
      Executable="$targetnametoken$.exe"
      EntryPoint="$targetentrypoint$">
    <uap:VisualElements
        AppListEntry="default"
        DisplayName="Dudu Desktop"
        Description="Dudu Desktop Companion"
        Square44x44Logo="PackageAssets\\Logo.png"
        Square150x150Logo="PackageAssets\\Logo.png"
        BackgroundColor="#F4EEE8" />
  </Application>
  ```

  Keep the manifest's final identity values synchronized with Partner Center. Do not commit a local/test identity to the Store workflow.

- [ ] **Step 5: Add package visual assets without touching `assets/raw/`**

  Create the package logo from the already tracked fallback/private visual asset using a deterministic Windows-side conversion step or checked-in derived PNGs. Include only the package logo files needed by the manifest; do not include source files, provenance notes, or raw artwork in the package asset directory.

- [ ] **Step 6: Run the cross-platform contract test**

  Run:

  ```powershell
  pwsh tests/scripts/store-package.tests.ps1
  ```

  Expected result: PASS. The test must not require Visual Studio, MSIX tooling, a Store account, or a Windows host.

- [ ] **Step 7: Commit the packaging contract**

  ```powershell
  git add src/Dudu.App/Dudu.App.csproj src/Dudu.App/Package.appxmanifest src/Dudu.App/PackageAssets tests/scripts/store-package.tests.ps1
  git commit -m "feat: add opt-in Store MSIX packaging"
  ```

---

### Task 3: Add package build and artifact validation

**Files:**
- Create: `scripts/package-store.ps1`
- Modify: `tests/scripts/release-contract.tests.ps1`
- Test: `tests/scripts/store-package.tests.ps1`

**Interfaces:**
- Consumes: `DuduStorePackage=true`, the existing Release build, and the manifest from Task 2.
- Produces: `artifacts/store-package/DuduDesktop-<version>-win-x64.msix` or the exact Store upload format produced by the Windows SDK, plus an ignored validation report.

- [ ] **Step 1: Write the failing script contract assertions**

  Require that `scripts/package-store.ps1` contains the exact safety contracts:

  ```powershell
  Assert-Contains "Store package script requires Windows" $storeScript "OperatingSystem.*Windows"
  Assert-Contains "Store package script enables the package mode" $storeScript "DuduStorePackage.*true"
  Assert-Contains "Store package script targets win-x64" $storeScript "RuntimeIdentifier.*win-x64"
  Assert-Contains "Store package script disables local signing" $storeScript "AppxPackageSigningEnabled.*false"
  Assert-Contains "Store package script writes beneath artifacts" $storeScript "artifacts[/\\]store-package"
  Assert-True "Store package script does not publish a relay URL" ($storeScript -notmatch "workers\.dev|DUDU_RELAY_BASE_URL")
  Assert-True "Store package script does not embed credentials" ($storeScript -notmatch "clientSecret|accessToken|password|token\s*=")
  ```

- [ ] **Step 2: Run the script contract test and verify it fails**

  Run:

  ```powershell
  pwsh tests/scripts/release-contract.tests.ps1
  ```

  Expected result: FAIL because `scripts/package-store.ps1` does not yet exist.

- [ ] **Step 3: Implement the Windows-only package wrapper**

  The script must:

  - declare `#Requires -Version 7.4`;
  - reject non-Windows hosts before invoking `dotnet`;
  - accept `-Version` and validate `Major.Minor.Patch` input;
  - resolve the repository root from the script location;
  - create only `artifacts/store-package/` and `artifacts/store-package-metadata/` beneath `artifacts/`;
  - invoke `dotnet publish src/Dudu.App/Dudu.App.csproj -c Release -r win-x64 --self-contained true -p:DuduStorePackage=true -p:Version=<version> -p:AppxPackageSigningEnabled=false`;
  - fail on a missing package or more than one package candidate;
  - write package SHA-256, package version, package identity, `dotnet --info`, and the resolved package file list to the ignored metadata directory;
  - never upload to Partner Center or modify Cloudflare state;
  - never delete anything outside the two exact artifact directories it created.

  Use `Get-FileHash -Algorithm SHA256` for the package hash and emit a lowercase `<hash>  <filename>` line. Keep the package artifact and metadata separate from the existing Inno artifact.

- [ ] **Step 4: Add package-level validation**

  On Windows, invoke `makeappx.exe`/the Windows SDK package validation available to the supported Visual Studio installation, then run Windows App Certification Kit validation when installed. Fail the script on invalid manifest, package identity mismatch, unsupported architecture, or missing package resources. Record the tool exit codes and summary under ignored metadata without copying logs containing secrets.

- [ ] **Step 5: Run the script contract test**

  Run:

  ```powershell
  pwsh tests/scripts/store-package.tests.ps1
  pwsh tests/scripts/release-contract.tests.ps1
  ```

  Expected result: PASS. On macOS, do not run the package wrapper; the cross-platform tests are the only local coverage for the wrapper body.

- [ ] **Step 6: Commit the package build wrapper**

  ```powershell
  git add scripts/package-store.ps1 tests/scripts/release-contract.tests.ps1 tests/scripts/store-package.tests.ps1
  git commit -m "build: add Store package artifact validation"
  ```

---

### Task 4: Preserve data, startup, notifications, and activation across packaging

**Files:**
- Modify: `src/Dudu.App/Hosting/AppPaths.cs` only if a regression is found
- Modify: `src/Dudu.App/System/StartupRegistrationService.cs` only if packaged launch requires a compatibility branch
- Modify: `src/Dudu.App/Notifications/NotificationActivation.cs` only if package activation changes the existing argument path
- Create: `tests/Dudu.App.Tests/Hosting/PackagedDataPathTests.cs`
- Create: `tests/Dudu.App.Tests/System/PackagedStartupRegistrationTests.cs`
- Modify: `tests/Dudu.App.Tests/Ui/XamlContractTests.cs` only for package-resource contract coverage

**Interfaces:**
- Consumes: the existing AppPaths, startup shortcut, notification activation, and bootstrap seams.
- Produces: explicit regression coverage for the packaged process without changing Core or Infrastructure dependencies.

- [ ] **Step 1: Write data-path regression tests**

  Add tests with the exact expectations:

  ```csharp
  [Fact]
  public void Explicit_data_root_is_independent_of_package_install_location()
  {
      var root = Path.Combine(Path.GetTempPath(), $"dudu-app-path-{Guid.NewGuid():N}");
      try
      {
          var paths = AppPaths.ForRoot(root);
          Assert.Equal(Path.Combine(root, "dudu.db"), paths.Database);
          Assert.Equal(Path.Combine(root, "backups"), paths.Backups);
          Assert.Equal(Path.Combine(root, "secrets"), paths.Secrets);
      }
      finally
      {
          if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
      }
  }
  ```

  Preserve the existing environment-variable override test and add a Windows acceptance note that the default root resolves to `%LocalAppData%\\DuduDesktop`, not the MSIX package install directory.

- [ ] **Step 2: Write startup and activation regression tests**

  Assert that startup registration continues to write only the current-user Startup shortcut, uses the packaged executable path when the app is running packaged, and passes `--background`. Assert that notification activation parsing remains independent of package identity and accepts the existing `action`, `messageId`, and `reminderId` forms without logging content.

- [ ] **Step 3: Run the focused App tests and verify any missing seam**

  On Windows run:

  ```powershell
  dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj -c Release --no-restore
  ```

  Expected result after the test additions: either PASS with no runtime changes, or a focused failure identifying the packaged startup/activation seam that must be repaired. Do not broaden the fix into a UI or hosting rewrite.

- [ ] **Step 4: Implement only the required compatibility change**

  If a packaged runtime cannot use the existing `.lnk` target or activation path, add a narrow Windows-only adapter at the existing service boundary. Keep `AppPaths` data outside the package, keep DPAPI under the same user profile, and retain the current uninstall behavior that does not delete data by default.

- [ ] **Step 5: Run the focused tests again**

  ```powershell
  dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj -c Release --no-restore
  ```

  Expected result: PASS with zero failures. Record any platform-limited skipped rows in `docs/testing/windows-acceptance.md` rather than weakening assertions.

- [ ] **Step 6: Commit the compatibility coverage**

  ```powershell
  git add src/Dudu.App tests/Dudu.App.Tests
  git commit -m "test: preserve user state across Store packaging"
  ```

---

### Task 5: Add the Store package to Windows CI without replacing Inno

**Files:**
- Modify: `.github/workflows/windows-installer.yml`
- Modify: `scripts/verify.ps1`
- Modify: `tests/scripts/release-contract.tests.ps1`
- Test: `tests/scripts/store-package.tests.ps1`

**Interfaces:**
- Consumes: the package wrapper from Task 3 and the existing Windows test/build jobs.
- Produces: a private `DuduDesktop-<version>-win-x64-store` CI artifact that is available for manual Partner Center upload, while the existing Inno artifact continues to be built and verified.

- [ ] **Step 1: Add failing workflow contract assertions**

  Require:

  ```powershell
  Assert-Contains "workflow invokes the Store package wrapper" $workflow "scripts/package-store\.ps1"
  Assert-Contains "workflow uploads a Store package artifact" $workflow "DuduDesktop-[^\" ]*-win-x64-store"
  Assert-Contains "workflow keeps the Inno artifact" $workflow "DuduDesktop-1\.0\.0-win-x64-private"
  Assert-True "Store package job does not expose secrets in logs" ($workflow -notmatch "echo.*STORE|Write-Host.*STORE.*SECRET")
  ```

- [ ] **Step 2: Run the release contract test and verify it fails**

  ```powershell
  pwsh tests/scripts/release-contract.tests.ps1
  ```

  Expected result: FAIL because the workflow has no Store package job.

- [ ] **Step 3: Add a separate Windows package job**

  Add a job after the existing Windows build/test prerequisites that:

  - uses the same pinned checkout/setup actions and `windows-latest` x64 runner;
  - restores the solution using the pinned SDK;
  - depends on the Windows test job succeeding;
  - runs `pwsh scripts/package-store.ps1 -Version 1.0.0`;
  - uploads only `artifacts/store-package/` and `artifacts/store-package-metadata/` under a private artifact name;
  - does not invoke `wrangler`, publish relay state, or print Partner Center credentials;
  - keeps all action references pinned to full commit SHAs as required by the existing workflow contract.

- [ ] **Step 4: Keep the existing Inno release gate authoritative until migration acceptance**

  Do not remove `scripts/publish-windows.ps1`, the Inno smoke test, the existing checksum verification, or the private asset allowlist. The Store package is an additional artifact until Task 8 proves the recipient-facing migration.

- [ ] **Step 5: Add a local release-contract invocation**

  Keep the Mac-safe script tests in the existing contract suite. Do not make `scripts/verify.ps1` invoke Windows-only MSIX tooling on macOS. On Windows, `scripts/verify.ps1` may call `scripts/package-store.ps1` after the existing Inno gate, but it must still fail fast and keep package output under `artifacts/`.

- [ ] **Step 6: Run cross-platform contract checks**

  ```powershell
  pwsh tests/scripts/release-contract.tests.ps1
  pwsh tests/scripts/store-package.tests.ps1
  git diff --check
  ```

  Expected result: PASS with no tracked generated artifacts.

- [ ] **Step 7: Commit the CI artifact path**

  ```powershell
  git add .github/workflows/windows-installer.yml scripts/verify.ps1 tests/scripts/release-contract.tests.ps1
  git commit -m "ci: publish private Store package artifact"
  ```

---

### Task 6: Document Partner Center submission and the two release paths

**Files:**
- Modify: `README.md`
- Modify: `docs/release.md`
- Modify: `docs/testing/windows-acceptance.md`
- Modify: `CHANGELOG.md`
- Test: `tests/scripts/release-contract.tests.ps1`

**Interfaces:**
- Consumes: the package artifact name and versioning behavior from Tasks 3 and 5.
- Produces: a private, reproducible release runbook that tells the owner exactly how to publish the next Store update and how to recover with Inno during migration.

- [ ] **Step 1: Add the Store release section to `docs/release.md`**

  Document these exact operations:

  1. reserve/maintain the private-audience Store app;
  2. run the green Windows workflow;
  3. download the Store package artifact to a temporary directory;
  4. compare its hash against the CI job summary and metadata;
  5. upload the MSIX/MSIX upload file to Partner Center;
  6. keep the audience private and submit for certification;
  7. after publication, verify the recipient's Store account can see and install it;
  8. submit later versions with strictly increasing package versions;
  9. pause a bad rollout and submit a corrected package;
  10. retain the Inno installer as the recovery path until two Store versions have upgraded successfully.

  State clearly that Store submissions are Microsoft-signed, while the current Inno artifact remains unsigned and may trigger SmartScreen/Smart App Control behavior.

- [ ] **Step 2: Add the package migration warning**

  Explain that a package identity change can affect startup shortcuts, notifications, activation, and uninstall behavior even when `%LocalAppData%\\DuduDesktop` is preserved. Require the Windows acceptance matrix before calling the Store path the normal recipient release.

- [ ] **Step 3: Add the recipient-facing instructions**

  Write a short private-release procedure: sign into Microsoft Store with the invited personal account, open the private Store link, install Dudu Desktop, leave Store app updates enabled, and do not delete local data when removing an old Inno installation unless intentionally wiping it from Dudu's Privacy & Data page.

- [ ] **Step 4: Add acceptance rows**

  Add rows for private Store visibility, first install, Store upgrade, retained data, pairing, startup, notification activation, Smart App Control enforcement, uninstall/reinstall, and a second Store update. Mark them pending until real Windows evidence exists; do not claim CI artifact creation proves recipient acceptance.

- [ ] **Step 5: Add source-level privacy assertions**

  Require the docs and workflow to contain no relay URL, sender URL, pairing code, token, private key, or raw-artwork path. Keep the tests fixed-string/source-contract based and avoid reading secrets from the environment in test output.

- [ ] **Step 6: Run documentation and contract checks**

  ```powershell
  pwsh tests/scripts/release-contract.tests.ps1
  git diff --check
  ```

  Expected result: PASS.

- [ ] **Step 7: Commit the runbook**

  ```powershell
  git add README.md docs/release.md docs/testing/windows-acceptance.md CHANGELOG.md tests/scripts/release-contract.tests.ps1
  git commit -m "docs: document private Store release flow"
  ```

---

### Task 7: Run the Mac-safe verification gate

**Files:**
- Modify: none unless a test exposes a regression in the implementation tasks.

**Interfaces:**
- Consumes: all repository changes from Tasks 2–6.
- Produces: evidence that the packaging additions do not break the supported macOS authoring workflow.

- [ ] **Step 1: Select the vendored toolchain**

  ```zsh
  export DUDU_REPO="$(git rev-parse --show-toplevel)"
  export DUDU_DOTNET="$DUDU_REPO/work/dotnet-sdk/dotnet"
  test -x "$DUDU_DOTNET"
  "$DUDU_DOTNET" --version
  ```

- [ ] **Step 2: Run the relay checks with the vendored SDK**

  ```zsh
  cd "$DUDU_REPO/relay"
  npm ci
  npm run typecheck
  DUDU_DOTNET="$DUDU_DOTNET" npm test -- --run
  npm run test:e2e
  cd "$DUDU_REPO"
  ```

- [ ] **Step 3: Restore and run the documented Mac stub builds**

  Use the exact `DUDU_MAC_STUB_FLAGS` from `AGENTS.md` for `Dudu.App`, `Dudu.App.Tests`, `Dudu.UiTests`, and `Dudu.WindowsHarness`. The MSIX property must remain off in this mode.

- [ ] **Step 4: Run the host-runnable Core and Infrastructure tests**

  Build and execute the generated xUnit binaries exactly as documented in `AGENTS.md`. Require zero failures and record only the expected platform skips.

- [ ] **Step 5: Run package/source contract tests**

  ```zsh
  "$DUDU_PWSH" tests/scripts/store-package.tests.ps1
  "$DUDU_PWSH" tests/scripts/release-contract.tests.ps1
  "$DUDU_PWSH" tests/scripts/publish-manifest.tests.ps1
  "$DUDU_PWSH" tests/scripts/verify.tests.ps1
  ```

- [ ] **Step 6: Verify repository cleanliness constraints**

  ```zsh
  find . -name packages.lock.json -print
  git status --short
  git diff --check
  ```

  Move generated lock files only into ignored `work/generated-package-locks/` as required by `AGENTS.md`. Do not stage the existing unrelated audit-fix changes.

- [ ] **Step 7: Commit only test/documentation corrections**

  If this task finds a packaging regression, fix it in a focused commit. Do not commit generated package files, Store artifacts, certificates, or Partner Center exports.

---

### Task 8: Perform the Windows and private-audience acceptance run

**Files:**
- Modify: `docs/testing/windows-acceptance.md` with dated evidence only
- Modify: `docs/release.md` only if an observed supported behavior requires clarification

**Interfaces:**
- Consumes: the green Windows CI Store artifact and the existing Inno-installed MVP.
- Produces: recipient-facing evidence that the Store path is safe to adopt.

- [ ] **Step 1: Install the existing MVP in an isolated test profile**

  Install the current Inno artifact with a test data root only. Record the app version, database path, pairing state, startup setting, notification registration, and a known local note/reminder.

- [ ] **Step 2: Install the private Store package**

  Sign into the Microsoft Store with the invited personal account, open the private link, install Dudu Desktop, and confirm Smart App Control is in enforcement mode before launch. Record whether Windows blocks or permits the package.

- [ ] **Step 3: Verify state migration**

  Confirm the Store build sees the expected local database, preferences, notes, reminders, backup files, DPAPI secrets, and pairing state. Confirm the process is not reading from the MSIX package directory as its durable data root.

- [ ] **Step 4: Verify Windows integration**

  Exercise overlay, tray, launch-at-sign-in, notification activation, settings window singleton behavior, high contrast, scaling, reduced motion, and clean shutdown. Confirm no duplicate startup process or duplicate settings window appears.

- [ ] **Step 5: Publish a second private Store version**

  Make a harmless versioned change, run the complete Windows package/test gate, submit the new package to the same private audience, and wait for publication. Confirm the Store offers the update and that the app/data remain usable after the upgrade.

- [ ] **Step 6: Verify recovery behavior**

  Confirm uninstall retains `%LocalAppData%\\DuduDesktop`, reinstall recovers the data, and the documented Inno fallback remains usable. Do not run a destructive local wipe as part of this test unless the test profile is disposable.

- [ ] **Step 7: Record evidence and make the release decision**

  Mark the acceptance matrix rows with date, Windows build, package version, result, and evidence location. Only after two Store versions have upgraded successfully may the Store path become the normal recipient release path; until then, send the Inno artifact only through a private channel with its separately verified checksum.

- [ ] **Step 8: Commit acceptance documentation**

  ```powershell
  git add docs/testing/windows-acceptance.md docs/release.md
  git commit -m "docs: record private Store acceptance"
  ```

---

## Plan self-review

- **Spec coverage:** package choice, private audience, data-path preservation, versioning, CI, security/privacy, rollback, Windows acceptance, and Inno fallback are covered by Tasks 1–8.
- **Completeness scan:** every implementation step has a concrete file, command, assertion, or acceptance result. Partner Center identity values are an explicit external prerequisite because they do not exist in the repository yet.
- **Type/interface consistency:** the package mode is consistently named `DuduStorePackage=true`; the package wrapper is consistently `scripts/package-store.ps1`; the package artifact is consistently under `artifacts/store-package/`; the test is consistently `tests/scripts/store-package.tests.ps1`.
- **Dirty-worktree safety:** every commit command names only files belonging to the Store migration. Existing audit changes remain unstaged.
- **Platform boundary:** no Mac step invokes MSIX tooling; real packaging and acceptance are explicitly Windows-only.

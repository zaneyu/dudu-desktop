# Private Microsoft Store MSIX Distribution Design

**Status:** Proposed
**Date:** 2026-09-17

## 1. Goal

Make Dudu Desktop sustainable for a non-technical single recipient:

- the owner can build and publish new versions from the existing Windows CI
  pipeline;
- the girlfriend can install the current MVP without managing developer tools;
- future versions arrive through the Microsoft Store with no manually copied
  installer and no Smart App Control exception;
- local notes, reminders, preferences, pairing state, secrets, and backups
  survive application upgrades.

The selected distribution model is a Microsoft Store MSIX package published to
a private audience containing the recipient's personal Microsoft account.
Microsoft re-signs Store-submitted MSIX packages and the Store manages update
delivery. Microsoft currently documents free individual Store registration and
free signing/hosting for Store MSIX submissions.

## 2. Current state and constraints

The current desktop is a WinUI 3 Windows App SDK application with:

- `WindowsPackageType=None`;
- self-contained `win-x64` publishing;
- a current-user Inno Setup installer;
- application data rooted at `%LocalAppData%\\DuduDesktop` by default;
- a private relay and private-use artwork;
- a Windows GitHub Actions workflow that builds and verifies the installer.

The current Inno installer is a useful fallback during migration, but it is not
the long-term update channel. It is unsigned and can be blocked by Smart App
Control. The relay's encrypted-note protocol and its data-retention/security
boundaries are unrelated to package distribution and must not be weakened to
support Store publishing.

Existing uncommitted audit-fix changes are out of scope for this migration. The
implementation must not stage, reset, or rewrite them.

## 3. Chosen approach

Add MSIX packaging to the existing WinUI application, preferably using the
Windows App SDK single-project MSIX model if it builds reliably with this
repository's SDK and package versions. If single-project packaging is
incompatible with the current project, add a dedicated Windows Application
Packaging Project that references `Dudu.App`; do not put packaging concerns in
`Dudu.Core` or `Dudu.Infrastructure`.

The Store submission will be configured as:

- free app;
- private audience;
- x64 package targeting the repository's supported Windows 11 baseline;
- not public and not discoverable outside the private audience;
- recipient access granted through the personal Microsoft account associated
  with the Store account she uses on Windows.

The Store is the update mechanism. Do not add an R2 installer bucket, a custom
desktop updater, or an embedded GitHub credential in this phase. The Store
checks for package updates and installs accepted Store submissions; Dudu may
provide release notes or a “check Store for updates” link later, but it should
not download or execute an installer itself.

## 4. Package and data compatibility

The package identity and installation directory may change, but the durable
data contract must not. `AppPaths` must continue to resolve the default data
root to `%LocalAppData%\\DuduDesktop`, independent of whether the process is
packaged. The implementation must verify this on a real Windows machine for:

1. upgrade from the existing Inno-installed MVP to the first Store build;
2. upgrade from one Store build to the next;
3. uninstall while retaining data;
4. reinstall after uninstall and recovering the retained data;
5. backup and restore across the packaging transition.

The migration must preserve or deliberately re-establish:

- SQLite database and migration behavior;
- DPAPI-protected secrets for the same Windows user;
- startup-at-sign-in behavior;
- notification activation;
- overlay and tray behavior;
- private asset loading;
- app shortcuts and uninstall semantics.

If Windows package identity changes the activation or startup paths, add a
compatibility bridge and test it. Do not silently change the data root or delete
the old Inno installation's data.

## 5. Versioning and release flow

Use the existing SemVer-like application version as the source of truth, while
also generating the four-part numeric MSIX package version required by the
Store. The mapping must be deterministic and documented; ordinary patch,
minor, and major releases must always produce a strictly increasing package
version.

The Windows workflow should be split conceptually into:

1. restore and build;
2. Core/Infrastructure/App tests and existing release contract checks;
3. build the MSIX/MSIX upload package;
4. run package validation and Windows App Certification Kit checks where the
   supported runner permits;
5. upload the package to Partner Center or produce the exact Store-upload
   artifact;
6. publish only after the existing release gates pass.

Store submission credentials must be GitHub Actions secrets or an equivalent
protected CI mechanism. They must never be placed in the repository, the app,
the relay, logs, or release artifacts. Manual Partner Center publishing is
acceptable initially; automate submission only after the package has passed a
manual private-audience release.

The current Inno workflow remains available until the first Store version has
been installed and upgraded successfully on the recipient's Windows machine.
After that acceptance point, Inno becomes a recovery/development artifact, not
the normal recipient update path.

## 6. Update and rollback behavior

The Store owns update scheduling and package installation. The app must not
assume that an update is immediately installed or that it can control the
Store's restart prompt. Release notes should describe whether a restart is
needed.

Before publishing a schema-changing version:

- migration tests must pass;
- automatic pre-migration backups must remain enabled;
- restore from the previous version's backup must be tested;
- the new version must tolerate the previous version's database state.

If a Store submission is bad, stop its rollout or submit a corrected package
through Partner Center. The local data-recovery path remains the existing
backup/restore flow. Do not add an application-level binary rollback that can
execute an unverified downloaded file.

## 7. Security and privacy

MSIX packages submitted to the Store do not require a privately purchased
CA-trusted certificate; Microsoft re-signs them for Store distribution. This
addresses Smart App Control trust for the Store-installed package. It does not
make the private artwork or relay data public.

The Store listing and private-audience configuration must avoid exposing:

- the relay URL;
- sender-page URLs;
- private Dudu artwork beyond what is required by the private listing;
- private release metadata;
- note content, tokens, pairing codes, or cryptographic material.

The app's existing privacy policy must accurately describe relay behavior and
the Store listing must link to it. No update endpoint should receive note text
or telemetry merely because the app is packaged.

## 8. Testing

### Mac-safe checks

- retain the existing relay and Core/Infrastructure verification path;
- verify packaging-related project files do not break the documented Mac stub
  builds;
- verify no generated MSIX, package certificates, Store metadata, or lock files
  are committed.

### Windows checks

On a real Windows 11 24H2 x64 machine, test:

- install from the private Store audience;
- launch, tray, overlay, notifications, startup, and shutdown;
- first-run onboarding and existing-user startup;
- upgrade from the Inno MVP to the Store package;
- upgrade across at least two Store submissions;
- database migration and pre-migration backup;
- pairing and encrypted note delivery after upgrade;
- uninstall with data retained;
- reinstall and data recovery;
- Smart App Control enabled in enforcement mode;
- Windows scaling, high contrast, and reduced motion;
- no duplicate startup process or duplicate settings window.

The release is not complete until the recipient-facing upgrade path has been
tested on Windows. A successful MSIX build on macOS is not sufficient evidence.

## 9. Non-goals

This phase does not:

- make Dudu public;
- support multiple recipients or accounts;
- add a custom update server;
- add silent self-updates;
- migrate the relay to another provider;
- redesign the application UI;
- permit the recipient to modify source code or release builds.

## 10. Acceptance criteria

The migration is accepted when:

1. the app builds as a Store-submittable x64 MSIX on the supported Windows CI;
2. a private-audience Store listing can install it on the recipient's machine;
3. Smart App Control does not block the Store-installed package;
4. a later Store submission updates the installed app without manual installer
   transfer;
5. all existing local data and pairing state survive the upgrade;
6. the current Inno build remains available as a documented recovery path until
   criterion 4 is proven;
7. CI and repository checks continue to reject private-data leakage and tracked
   generated artifacts.

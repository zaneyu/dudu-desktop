# Task 6 review fix round

## Status

Implemented the requested review fixes in the existing Task 6 scope. The
existing Inno Setup workflow and packaging path were left intact.

## Fixes

- The Store runbook now fails closed: the local `DuduDesktop.Local.NonProduction`
  identity is explicitly prohibited from Partner Center upload. It documents
  the owner-only exact `Identity Name`/`Publisher` configuration and the
  `-RequirePartnerCenterIdentity` gate; only a package produced after that
  successful gate may be submitted.
- The Store CI job writes the package version and SHA-256 to
  `GITHUB_STEP_SUMMARY`. The runbook now identifies the `.msix` package and
  requires comparing its hash with both the summary and metadata hash.
- `workflow_dispatch` accepts an explicit `store_version`; push runs retain a
  reproducible `1.0.0` default, and the existing three-part package validation
  remains authoritative.
- Source privacy tests now reject concrete URLs generally, while allowing
  official Microsoft documentation hosts and placeholders. Fixed URL fixtures
  cover both sides of the rule; no secrets are read.
- Runbook wording distinguishes the raw `.msix` package from a Partner Center
  upload container.

## Verification

On macOS, using the vendored PowerShell runtime:

- `tests/scripts/release-contract.tests.ps1` — PASS, 93 cases.
- `tests/scripts/store-package.tests.ps1` — PASS, 26 cases.
- `git diff --check` — PASS.

The Windows-only `scripts/package-store.ps1`, MSIX validation, WACK, Inno
packaging, and Store acceptance were not run on macOS.

## Concerns

The submission identity values are intentionally absent from source control.
The owner must configure them in a private working copy before producing a
submission package. A CI artifact built with the local identity remains
acceptance-only and must not be uploaded.

## Follow-up review fix

Clarified the sequence so the CI package is used only for acceptance and hash
verification. The owner separately configures the exact Partner Center
manifest identity, runs the identity-gated local command, verifies that local
package against its own metadata and intended version, and uploads only that
package. Contract tests now require the script's explicit local-identity
rejection condition and check that the docs and workflow label the CI artifact
acceptance-only and forbid Partner Center upload.

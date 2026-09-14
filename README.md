# Dudu Desktop Companion

Dudu Desktop Companion is a private-use Windows 11 desktop companion for x64
systems. Task 1 establishes the .NET solution and keeps the dependency
direction explicit: `Dudu.App` references `Dudu.Infrastructure`, which
references `Dudu.Core`.

## Development prerequisites

- Windows 11 version 24H2, build 26100 or newer, on x64.
- .NET SDK 10.0.112.
- Visual Studio 2026 with the WinUI workload installed.
- Node.js 24.21.0 for the sender and relay work added later.

Restore and build from the repository root with:

```text
dotnet restore DuduDesktop.slnx
dotnet build DuduDesktop.slnx -c Release
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj -c Release
```

The application is intended for private use. The collected Dudu artwork and
the initial private asset pack are private-use-only and must not be redistributed,
published, or included in a public asset marketplace.

## Documents

- [Design specification](docs/superpowers/specs/2026-09-11-dudu-desktop-companion-design.md) —
  the approved product and technical design.
- [Implementation plan](docs/superpowers/plans/2026-09-11-dudu-desktop-companion-implementation.md) —
  the task-by-task build plan this codebase follows.
- [Privacy](docs/privacy.md) — what data stays local, what the relay ever sees, and the privacy
  guarantees the encrypted remote-note flow relies on.
- [Windows acceptance evidence](docs/testing/windows-acceptance.md) — the manual and automated
  acceptance matrix that must be completed on a real Windows 11 24H2 x64 host before a release is
  considered fully verified.
- [Release runbook](docs/release.md) — prerequisites, Cloudflare relay setup, desktop build and
  install/upgrade/uninstall, pairing, backup/restore, and the private-use statements that govern
  this release.

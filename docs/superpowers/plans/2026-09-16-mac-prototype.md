# Mac Prototype Refactoring Plan

## Goal

Produce a runnable macOS development prototype without disturbing the Windows
WinUI application. The prototype must display the existing animated Dudu asset
pack in a transparent, always-on-top desktop window, support dragging and a
small action surface, and exercise one local reminder/behavior path.

The prototype is explicitly not a Mac feature-parity port. Remote notes,
notifications, login launch, global hotkeys, fullscreen detection, Keychain
secret storage, full settings navigation, signing, notarization, and release
packaging are deferred.

## Global Constraints

- Preserve the existing dependency direction: `Dudu.App` ->
  `Dudu.Infrastructure` -> `Dudu.Core`. New shared rendering code may be
  referenced by both platform shells, but Core must not reference UI or OS code.
- Keep the existing Windows app behavior and project target intact. The Mac
  prototype must not reference WinUI, Windows App SDK, Win32, `HWND`, or DPAPI.
- Do not change the relay protocol, relay routes, private asset provenance, or
  add new raw artwork. Reuse the already tracked asset-pack files.
- Do not commit generated files, lock files, build output, or artifacts.
- New production behavior is developed test-first where a deterministic test
  boundary exists. UI-only behavior may be covered by focused manual smoke
  instructions and a platform-neutral contract test.
- The first Mac build is a local development prototype, not a release artifact.
  Do not add signing/notarization or claim Windows acceptance from Mac.
- Use Luna subagents for implementation tasks, one fresh implementer per task,
  followed by a scoped task review. Do not run multiple implementation agents
  concurrently because later tasks consume earlier interfaces.

## Tasks

### Task 1: Extract shared animation/rendering library

Create a new `src/Dudu.Rendering` net10.0 library containing the platform-neutral
animation/frame composition code currently trapped in `Dudu.App/Animation`.
Move only the classes needed by both shells, preserve public behavior, update
the Windows app project reference/usings, and add focused tests for manifest
loading, fallback behavior, and frame timing where those behaviors are not
already covered. Add the project to `DuduDesktop.slnx` and ensure the Windows
project still builds in Mac stub mode.

### Task 2: Create the Mac prototype project and transparent pet host

Create `src/Dudu.MacPrototype` as a macOS desktop project using a C# desktop UI
stack suitable for a transparent window. Prefer Avalonia for the first spike
because it preserves .NET/Core reuse; if its macOS transparency cannot support
the required pet surface, isolate the fallback to a small AppKit interop host.
The project must reference `Dudu.Core` and `Dudu.Rendering`, must not reference
`Dudu.App`, and must provide a launchable transparent always-on-top window that
renders an idle frame and supports dragging. Add a minimal project-level smoke
entry point and include it in the solution only if it remains buildable on the
authoring Mac.

### Task 3: Add minimal local interaction loop

Extend the Mac prototype with a small dismissible action bubble and a minimal
local behavior composition. Reuse `PetStateMachine` and existing Core models
where practical. Support `Pet`, `Drink water`, `Start focus`, `Comfort me`, and
`Quit` actions, with a short local reminder/demo path and no remote network
dependency. Persist only the minimum prototype state needed for position and
basic preferences. Do not introduce a plaintext substitute for production
secret storage.

### Task 4: Verification, documentation, and Mac smoke checklist

Add a concise Mac prototype README/runbook covering prerequisites, launch
commands, supported architecture (`osx-arm64` or `osx-x64` as applicable), and
the manual smoke checklist. Run the focused tests, Mac-safe builds, and any
available project tests. Confirm the Windows projects remain unchanged in
their platform-specific behavior and report any checks that cannot run on the
Mac host.

## Acceptance Criteria

- `Dudu.MacPrototype` launches on the authoring Mac from the repository.
- A transparent Dudu overlay is visible, animated, draggable, and stays above
  ordinary application windows without taking keyboard focus.
- Clicking the pet exposes the minimal action surface; each listed action has
  an observable local result and the surface can be dismissed.
- The prototype works without Cloudflare, pairing, or network connectivity.
- Existing Core tests pass; shared rendering tests pass; Mac project builds;
  Windows code still passes the documented Mac stub build or any platform
  limitation is explicitly recorded.
- No generated files or private release artifacts are added to Git.

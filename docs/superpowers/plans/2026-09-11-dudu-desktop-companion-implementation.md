# Dudu Desktop Companion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a private Windows 11 x64 Dudu desktop companion with a transparent animated pet, local productivity and comfort tools, and end-to-end encrypted notes sent from a paired mobile web page.

**Architecture:** A self-contained .NET 10 application combines a WinUI 3 settings surface with a no-activate Win32 layered overlay, while focused domain services and SQLite provide local-first behavior. A separate TypeScript Cloudflare Worker with D1 relays browser-encrypted message envelopes; the desktop polls, decrypts locally, and never reports note-open events.

**Tech Stack:** C# 14, .NET SDK 10.0.112/.NET 10.0.12, Windows App SDK 2.3.1, WinUI 3, CsWin32 0.3.333, SkiaSharp 4.151.1, Microsoft.Data.Sqlite 10.0.12, CommunityToolkit.Mvvm 8.4.2, xUnit v3 4.0.0, Node.js 24.21.0 LTS, TypeScript 7.0.2, Cloudflare Workers/D1, Wrangler 4.131.1, Vitest 5.0.0, `@cloudflare/vitest-plugin` 1.1.8, Playwright 1.63.0, and Inno Setup 7.1.0.

## Global Constraints

- Support Windows 11 version 24H2, build 26100 or newer, on x64 only.
- Target .NET 10 LTS and pin Windows App SDK 2.3.1 as approved in the design specification.
- Ship a self-contained, unpackaged, per-user installation under `%LOCALAPPDATA%\Programs\DuduDesktop`.
- Store application data under `%LOCALAPPDATA%\DuduDesktop` and require no administrator rights.
- Keep reminders, tasks, focus, countdowns, comfort mode, outfits, check-ins, and local notes fully usable offline.
- Use a WinUI 3 settings window and a separate no-activate Win32 layered overlay in the same desktop process.
- Keep the overlay out of Alt+Tab and the taskbar and never steal keyboard focus.
- Use SQLite as the local system of record and create a recoverable backup before every schema migration.
- Protect desktop private keys and capability tokens with current-user Windows DPAPI.
- Use polling, not WNS or Azure infrastructure, for remote-note delivery.
- Encrypt every remote-note payload in the browser with ephemeral P-256 ECDH, HKDF-SHA-256, and AES-256-GCM.
- Never persist remote-note plaintext by default; never send plaintext, private keys, note-open events, or read receipts to Cloudflare.
- Display only `A note arrived 💌` until the recipient intentionally opens a note.
- Delete acknowledged ciphertext from D1 and expire undelivered ciphertext after 30 days.
- Apply pet-state priority in this order: manual comfort, unread remote note, due reminder, focus transition, welcome-back, ambient, idle.
- Make all Dudu art replaceable through a versioned asset-pack manifest and label the initial pack private-use-only and non-redistributable.
- Include no AI chat, voice, camera, microphone, location, screen reading, mood inference, analytics, advertising, public accounts, or public distribution.
- Use `CancellationToken` on asynchronous C# I/O boundaries and `AbortSignal` on browser fetch boundaries.
- Use UTC for persistence and protocol timestamps; convert to Windows local time only at presentation and recurrence-policy boundaries.
- Treat warnings as errors in production projects and keep nullable reference types enabled.
- Run commands from the repository root unless a step explicitly changes directory.

---

## Delivery milestones

1. **Local engine:** Tasks 1–8 produce a headless, fully tested local companion core with persistence.
2. **Windows pet:** Tasks 9–15 produce an installable offline desktop pet and modern settings experience.
3. **Private sender:** Tasks 16–20 add the encrypted Worker relay, mobile sender, and desktop synchronization.
4. **Release candidate:** Tasks 21–24 complete integration hardening, packaging, accessibility, and release evidence.

Each milestone must pass its listed verification before work proceeds to the next milestone.

## Repository file map

### Root and build

- `DuduDesktop.slnx` — solution graph.
- `global.json` — pins .NET SDK 10.0.112.
- `Directory.Build.props` — nullable, analyzers, deterministic builds, and Windows target defaults.
- `Directory.Packages.props` — central NuGet versions.
- `.editorconfig` — C# and XAML formatting.
- `.gitignore` — .NET, Visual Studio, Node, Wrangler, test, and release outputs.
- `README.md` — developer setup and private-use warning.
- `scripts/verify.ps1` — one-command restore, build, test, web test, and publish verification.

### Domain and application core

- `src/Dudu.Core/Dudu.Core.csproj` — platform-light domain assembly.
- `src/Dudu.Core/Time/IClock.cs` — injectable UTC clock and local zone.
- `src/Dudu.Core/Time/SystemClock.cs` — production clock.
- `src/Dudu.Core/Models/*.cs` — profile, preferences, reminders, tasks, focus sessions, notes, check-ins, countdowns, asset metadata, and placement records.
- `src/Dudu.Core/Policies/QuietHoursPolicy.cs` — quiet-hours eligibility.
- `src/Dudu.Core/Pet/PetStateMachine.cs` — deterministic state priority and interruption.
- `src/Dudu.Core/Reminders/ReminderScheduler.cs` — recurrence and missed-occurrence calculation.
- `src/Dudu.Core/Focus/FocusService.cs` — durable focus lifecycle.
- `src/Dudu.Core/Notes/LocalNoteSelector.cs` — capped, nonrepeating local-note selection.
- `src/Dudu.Core/CheckIns/CheckInService.cs` — local-only manual check-ins and summaries.
- `src/Dudu.Core/Countdowns/CountdownService.cs` — countdown calculations.
- `src/Dudu.Core/Assets/AssetManifest.cs` — versioned asset contract and fallback rules.
- `src/Dudu.Core/Abstractions/*.cs` — repository, notification, secret, remote-relay, and presentation interfaces.

### Windows infrastructure

- `src/Dudu.Infrastructure/Dudu.Infrastructure.csproj` — SQLite, DPAPI, HTTP, and Windows infrastructure.
- `src/Dudu.Infrastructure/Data/Database.cs` — connections, migrations, and transactions.
- `src/Dudu.Infrastructure/Data/Migrations/*.sql` — versioned schema.
- `src/Dudu.Infrastructure/Data/Repositories/*.cs` — concrete repositories.
- `src/Dudu.Infrastructure/Data/DatabaseBackupService.cs` — bounded pre-migration backups and recovery.
- `src/Dudu.Infrastructure/Security/DpapiSecretStore.cs` — user-scoped secret protection.
- `src/Dudu.Infrastructure/Crypto/EnvelopeCrypto.cs` — desktop envelope decryption and validation.
- `src/Dudu.Infrastructure/Remote/RelayClient.cs` — typed relay HTTP client.
- `src/Dudu.Infrastructure/Remote/RemoteSyncService.cs` — polling, deduplication, local queueing, and acknowledgment.

### Windows application and overlay

- `src/Dudu.App/Dudu.App.csproj` — unpackaged WinUI 3 executable.
- `src/Dudu.App/App.xaml*` — application startup and resource composition.
- `src/Dudu.App/Hosting/AppHost.cs` — DI, lifecycle, and orderly shutdown.
- `src/Dudu.App/Hosting/SingleInstanceCoordinator.cs` — second-launch activation.
- `src/Dudu.App/Overlay/NativeMethods.txt` — CsWin32 API allowlist.
- `src/Dudu.App/Overlay/OverlayWindowHost.cs` — layered HWND and message handling.
- `src/Dudu.App/Overlay/OverlayHitTest.cs` — alpha-aware hit testing.
- `src/Dudu.App/Overlay/MonitorPlacementService.cs` — DPI-safe normalized placement.
- `src/Dudu.App/Animation/AssetManifestLoader.cs` — validation and frame loading.
- `src/Dudu.App/Animation/AnimationEngine.cs` — frame scheduling and Skia composition.
- `src/Dudu.App/Animation/LayeredFramePresenter.cs` — `UpdateLayeredWindow` bridge.
- `src/Dudu.App/Windows/SettingsWindow.xaml*` — custom-title-bar navigation shell.
- `src/Dudu.App/Pages/*.xaml*` — onboarding and seven approved settings pages.
- `src/Dudu.App/ViewModels/*.cs` — MVVM state and commands.
- `src/Dudu.App/Tray/TrayIconService.cs` — `Shell_NotifyIcon` menu and activation.
- `src/Dudu.App/System/FullscreenDetector.cs` — foreground-window/work-area comparison.
- `src/Dudu.App/System/StartupRegistrationService.cs` — current-user Startup shortcut.
- `src/Dudu.App/Notifications/AppNotificationService.cs` — generic private notifications.
- `src/Dudu.App/Assets/Packs/private-dudu/*` — normalized private artwork and manifest.
- `src/Dudu.App/Assets/Packs/fallback/*` — original neutral fallback pose.

### Asset preparation

- `tools/Dudu.AssetTool/Dudu.AssetTool.csproj` — deterministic private asset normalizer.
- `tools/Dudu.AssetTool/Program.cs` — import/normalize/validate commands.
- `tools/Dudu.AssetTool/AssetNormalizer.cs` — trim, resize, premultiply, and frame export.
- `assets/sources/private-dudu-sources.json` — URLs, attribution, checksums, access dates, and transformations.
- `assets/raw/.gitkeep` — ignored staging folder for downloaded source files.

### Relay and sender

- `relay/package.json` and `relay/package-lock.json` — exact Node dependency graph.
- `relay/wrangler.jsonc` — Worker, D1, static assets, and compatibility date.
- `relay/src/index.ts` — route entry point.
- `relay/src/routes/*.ts` — device, pairing, message, and sender-session handlers.
- `relay/src/security/*.ts` — token hashing, cookies, origin enforcement, rate limits, and request validation.
- `relay/src/db/*.ts` — typed D1 statements.
- `relay/migrations/*.sql` — D1 schema.
- `relay/public/index.html` — mobile sender shell.
- `relay/sender-src/*.ts` — browser pairing, encryption, message composer, and status client.
- `relay/tsconfig.sender.json` — emits browser ES modules to the ignored `relay/public/dist` directory.
- `relay/public/styles.css` — responsive sender design.
- `relay/test/*.spec.ts` — Worker runtime and D1 tests.
- `relay/e2e/*.spec.ts` — Playwright sender tests.

### Tests, installer, and release evidence

- `tests/Dudu.Core.Tests/*` — deterministic domain tests.
- `tests/Dudu.Infrastructure.Tests/*` — SQLite, DPAPI, crypto, and relay-client tests.
- `tests/Dudu.App.Tests/*` — manifest, animation, placement, fullscreen, and view-model tests.
- `tests/Dudu.UiTests/*` — FlaUI onboarding/settings automation.
- `tests/Dudu.WindowsHarness/*` — layered-window and notification manual/automated harness.
- `installer/DuduDesktop.iss` — current-user Inno Setup package.
- `installer/assets/*` — installer icon and wizard artwork.
- `docs/testing/windows-acceptance.md` — manual matrix and evidence fields.
- `docs/privacy.md` — precise data inventory and threat boundary.
- `docs/release.md` — private build, deployment, rollback, and uninstall procedure.

## Milestone 1 — Local engine

### Task 1: Establish the Windows solution and dependency boundaries

**Files:**
- Create: `DuduDesktop.slnx`
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `.editorconfig`
- Create: `.gitignore`
- Create: `src/Dudu.Core/Dudu.Core.csproj`
- Create: `src/Dudu.Infrastructure/Dudu.Infrastructure.csproj`
- Create: `src/Dudu.App/Dudu.App.csproj`
- Create: `tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj`
- Create: `tests/Dudu.Core.Tests/ProductInfoTests.cs`
- Create: `src/Dudu.Core/ProductInfo.cs`
- Create: `README.md`

**Interfaces:**
- Consumes: None.
- Produces: `ProductInfo.Name: string`, `ProductInfo.ProtocolVersion: int`, and the project-reference rule `Dudu.App -> Dudu.Infrastructure -> Dudu.Core`.

- [ ] **Step 1: Initialize an isolated repository and create the failing product identity test**

If the current directory is not already its own repository root, initialize a nested project repository so the unrelated home-level repository is never modified:

```powershell
if ((git rev-parse --show-toplevel 2>$null) -ne (Get-Location).Path) {
    git init -b main
}
git switch -c codex/windows-v1
dotnet new sln --name DuduDesktop --format slnx
dotnet new classlib -n Dudu.Core -o src/Dudu.Core -f net10.0
dotnet new classlib -n Dudu.Infrastructure -o src/Dudu.Infrastructure -f net10.0
dotnet new xunit -n Dudu.Core.Tests -o tests/Dudu.Core.Tests -f net10.0
```

Create `tests/Dudu.Core.Tests/ProductInfoTests.cs`:

```csharp
using Dudu.Core;

namespace Dudu.Core.Tests;

public sealed class ProductInfoTests
{
    [Fact]
    public void Product_identity_is_stable()
    {
        Assert.Equal("Dudu Desktop", ProductInfo.Name);
        Assert.Equal(1, ProductInfo.ProtocolVersion);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~ProductInfoTests`  
Expected: FAIL with `CS0103` or `CS0246` because `ProductInfo` does not exist.

- [ ] **Step 3: Add pinned build configuration, project boundaries, and minimal product identity**

Set `global.json` to:

```json
{
  "sdk": {
    "version": "10.0.112",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

Set central package versions in `Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.12" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />
    <PackageVersion Include="Microsoft.Windows.CsWin32" Version="0.3.333" />
    <PackageVersion Include="Microsoft.WindowsAppSDK" Version="2.3.1" />
    <PackageVersion Include="SkiaSharp" Version="4.151.1" />
    <PackageVersion Include="SkiaSharp.NativeAssets.Win32" Version="4.151.1" />
    <PackageVersion Include="System.Security.Cryptography.ProtectedData" Version="10.0.12" />
    <PackageVersion Include="xunit.v3" Version="4.0.0" />
    <PackageVersion Include="coverlet.collector" Version="10.0.1" />
    <PackageVersion Include="FlaUI.Core" Version="5.0.0" />
    <PackageVersion Include="FlaUI.UIA3" Version="5.0.0" />
  </ItemGroup>
</Project>
```

Set `Directory.Build.props` to enable nullable references, implicit usings, deterministic builds, `RestorePackagesWithLockFile=true`, and warnings as errors for `src`. Retarget `Dudu.Infrastructure`, `Dudu.App`, infrastructure tests, and app tests to `net10.0-windows10.0.26100.0`; keep `Dudu.Core` and core tests on `net10.0`. Configure `Dudu.App.csproj` with `UseWinUI=true`, `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, `RuntimeIdentifier=win-x64`, and `PlatformTarget=x64`. Replace test-template package references with central `xunit.v3`, `Microsoft.NET.Test.Sdk`, and `coverlet.collector` references. Add only the approved project references and all projects to `DuduDesktop.slnx`.

Create `src/Dudu.Core/ProductInfo.cs`:

```csharp
namespace Dudu.Core;

public static class ProductInfo
{
    public const string Name = "Dudu Desktop";
    public const int ProtocolVersion = 1;
}
```

Document Windows 11 24H2, Visual Studio 2026 with WinUI workload, Node 24.21.0, and private-use asset restrictions in `README.md`.

- [ ] **Step 4: Verify restore, dependency direction, and the passing test**

Run:

```powershell
dotnet restore DuduDesktop.slnx
dotnet build DuduDesktop.slnx -c Release
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj -c Release
```

Expected: all three commands exit 0; no warning is emitted; `Dudu.Core.Tests` reports 1 passed test.

- [ ] **Step 5: Commit**

```bash
git add DuduDesktop.slnx global.json Directory.Build.props Directory.Packages.props .editorconfig .gitignore README.md src tests docs/superpowers
git commit -m "build: scaffold Windows companion solution"
```

### Task 2: Define time, preferences, quiet hours, and pet presentation contracts

**Files:**
- Create: `src/Dudu.Core/Time/IClock.cs`
- Create: `src/Dudu.Core/Time/SystemClock.cs`
- Create: `src/Dudu.Core/Models/Preferences.cs`
- Create: `src/Dudu.Core/Models/Profile.cs`
- Create: `src/Dudu.Core/Models/PetPlacement.cs`
- Create: `src/Dudu.Core/Models/PetPresentation.cs`
- Create: `src/Dudu.Core/Policies/QuietHoursPolicy.cs`
- Create: `tests/Dudu.Core.Tests/Policies/QuietHoursPolicyTests.cs`

**Interfaces:**
- Consumes: `ProductInfo.ProtocolVersion`.
- Produces: `IClock.UtcNow`, `IClock.LocalTimeZone`, `QuietHoursPolicy.IsQuiet(DateTimeOffset, QuietHours, TimeZoneInfo): bool`, `QuietHoursPolicy.NextAllowedUtc(...)`, `Preferences`, `PetPlacement`, and `PetPresentation`.

- [ ] **Step 1: Write failing tests for a quiet interval that crosses midnight**

```csharp
using Dudu.Core.Models;
using Dudu.Core.Policies;

namespace Dudu.Core.Tests.Policies;

public sealed class QuietHoursPolicyTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly QuietHours Quiet = new(
        Enabled: true,
        Start: new TimeOnly(22, 0),
        End: new TimeOnly(7, 0));

    [Theory]
    [InlineData("2026-09-11T23:00:00Z", true)]
    [InlineData("2026-09-12T06:59:00Z", true)]
    [InlineData("2026-09-12T07:00:00Z", false)]
    [InlineData("2026-09-12T12:00:00Z", false)]
    public void IsQuiet_handles_midnight_crossing(string utc, bool expected)
    {
        Assert.Equal(expected, QuietHoursPolicy.IsQuiet(DateTimeOffset.Parse(utc), Quiet, Utc));
    }

    [Fact]
    public void NextAllowedUtc_returns_the_next_end_boundary()
    {
        var result = QuietHoursPolicy.NextAllowedUtc(
            DateTimeOffset.Parse("2026-09-11T23:00:00Z"), Quiet, Utc);

        Assert.Equal(DateTimeOffset.Parse("2026-09-12T07:00:00Z"), result);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~QuietHoursPolicyTests`  
Expected: FAIL because `QuietHours` and `QuietHoursPolicy` are undefined.

- [ ] **Step 3: Implement the contracts and quiet-hours policy**

Define:

```csharp
namespace Dudu.Core.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    TimeZoneInfo LocalTimeZone { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
}
```

Use immutable records for:

```csharp
public sealed record QuietHours(bool Enabled, TimeOnly Start, TimeOnly End);
public sealed record Profile(string RecipientName, bool OnboardingComplete);
public sealed record Preferences(
    AppTheme Theme,
    QuietHours QuietHours,
    bool ReducedMotion,
    int LocalNoteDailyLimit,
    bool LaunchAtSignIn,
    bool AlwaysOnTop,
    bool HidePetDuringFullscreen,
    TimeSpan AmbientMinimumInterval);
public sealed record PetPlacement(
    string MonitorDeviceName,
    double NormalizedX,
    double NormalizedY,
    double Scale);
public sealed record PetPresentation(
    PetState State,
    string AnimationKey,
    string? BubbleTitle,
    string? BubbleBody,
    bool RequiresRecipientAction);
```

`QuietHoursPolicy` converts UTC to the supplied zone, handles equal start/end as quiet all day when enabled, treats `Start < End` as same-day, treats `Start > End` as crossing midnight, and resolves the next local end boundary back to UTC using `TimeZoneInfo.GetAmbiguousTimeOffsets` and `IsInvalidTime`.

- [ ] **Step 4: Run focused and full core tests**

Run:

```powershell
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~QuietHoursPolicyTests
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj
```

Expected: both commands pass; the focused run reports 5 passed tests.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.Core tests/Dudu.Core.Tests
git commit -m "feat: define companion time and preference policies"
```

### Task 3: Implement the deterministic pet state machine

**Files:**
- Create: `src/Dudu.Core/Pet/PetEvent.cs`
- Create: `src/Dudu.Core/Pet/PetStateMachine.cs`
- Create: `src/Dudu.Core/Pet/AmbientScheduler.cs`
- Create: `src/Dudu.Core/Abstractions/IRandomSource.cs`
- Create: `tests/Dudu.Core.Tests/Pet/PetStateMachineTests.cs`

**Interfaces:**
- Consumes: `PetPresentation` and `IClock`.
- Produces: `PetStateMachine.Handle(PetEvent): PetPresentation`, `PetStateMachine.Current`, `AmbientScheduler.NextEligibleUtc`, and typed `PetEvent` records for comfort, note, reminder, focus, welcome-back, ambient, dismiss, pause, and resume.

- [ ] **Step 1: Write failing priority and interruption tests**

```csharp
using Dudu.Core.Pet;

namespace Dudu.Core.Tests.Pet;

public sealed class PetStateMachineTests
{
    [Fact]
    public void Manual_comfort_outranks_note_and_reminder()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.ReminderDue("medicine"));
        machine.Handle(new PetEvent.RemoteNoteArrived("m-1"));

        var result = machine.Handle(new PetEvent.ComfortRequested());

        Assert.Equal(PetState.Comfort, result.State);
        Assert.Equal("comfort-hug", result.AnimationKey);
    }

    [Fact]
    public void Ambient_event_is_discarded_while_focus_is_active()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.FocusStarted("f-1"));

        var result = machine.Handle(new PetEvent.AmbientRequested("wave"));

        Assert.Equal(PetState.Focus, result.State);
    }

    [Fact]
    public void Note_remains_pending_until_focus_ends()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.FocusStarted("f-1"));
        machine.Handle(new PetEvent.RemoteNoteArrived("m-1"));

        var result = machine.Handle(new PetEvent.FocusEnded("f-1"));

        Assert.Equal(PetState.RemoteNote, result.State);
        Assert.Equal("A note arrived 💌", result.BubbleTitle);
        Assert.Null(result.BubbleBody);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~PetStateMachineTests`  
Expected: FAIL because `PetStateMachine` and `PetEvent` do not exist.

- [ ] **Step 3: Implement queued durable events and fixed priority**

Represent `PetEvent` as a closed abstract record hierarchy. Internally track active manual state, pending remote message IDs, due reminder IDs, focus state, pending welcome-back, and one ambient request. Recompute after every event with this exact selection:

```csharp
private PetPresentation Select()
{
    if (_comfortActive)
        return Present(PetState.Comfort, "comfort-hug");
    if (!_focusActive && _remoteMessageIds.Count > 0)
        return new(PetState.RemoteNote, "note-arrival", "A note arrived 💌", null, true);
    if (!_focusActive && _dueReminderIds.Count > 0)
        return Present(PetState.Reminder, "reminder");
    if (_focusTransition is not null)
        return Present(PetState.FocusTransition, _focusTransition);
    if (_welcomeBackPending)
        return Present(PetState.WelcomeBack, "greeting");
    if (!_paused && !_focusActive && _ambientAnimation is not null)
        return Present(PetState.Ambient, _ambientAnimation);
    if (_focusActive)
        return Present(PetState.Focus, "focus");
    return Present(PetState.Idle, "idle");
}
```

`Dismissed` removes only the addressed durable item. `PauseRequested` suppresses welcome-back and ambient behavior but retains reminders and notes. `FocusEnded` clears the transition after one presentation acknowledgment and then reveals durable queued work.

`AmbientScheduler` accepts `IClock`, `IRandomSource`, and `Preferences.AmbientMinimumInterval`. It returns no event during quiet hours, pause, focus, fullscreen, session lock, or before the minimum interval, and otherwise selects only `idle`, `blink`, `wave`, or `sleep` with bounded random delay from 15 through 45 minutes.

- [ ] **Step 4: Run the state tests and core regression suite**

Run:

```powershell
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~PetStateMachineTests
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj
```

Expected: focused tests report 3 passed; the complete core suite passes.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.Core/Pet src/Dudu.Core/Abstractions/IRandomSource.cs tests/Dudu.Core.Tests/Pet
git commit -m "feat: add deterministic pet state machine"
```

### Task 4: Implement reminder recurrence, quiet-hour deferral, and missed-event reconciliation

**Files:**
- Create: `src/Dudu.Core/Models/Reminder.cs`
- Create: `src/Dudu.Core/Reminders/ReminderScheduler.cs`
- Create: `src/Dudu.Core/Reminders/ReminderOccurrencePolicy.cs`
- Create: `src/Dudu.Core/Reminders/ReminderEngine.cs`
- Create: `src/Dudu.Core/Abstractions/IReminderRepository.cs`
- Create: `src/Dudu.Core/Abstractions/IReminderDueSink.cs`
- Create: `tests/Dudu.Core.Tests/Reminders/ReminderSchedulerTests.cs`

**Interfaces:**
- Consumes: `IClock`, `QuietHoursPolicy`.
- Produces: `ReminderScheduler.NextOccurrence(Reminder, DateTimeOffset, TimeZoneInfo): DateTimeOffset?`, `ReminderScheduler.Reconcile(Reminder, DateTimeOffset, DateTimeOffset, TimeZoneInfo): ReminderReconciliation`, `ReminderEngine.TickAsync`, `RecurrenceRule`, and `ReminderOccurrencePolicy`.

- [ ] **Step 1: Write failing daily, weekday, interval, and sleep-recovery tests**

```csharp
[Fact]
public void Selected_weekdays_skips_unselected_days()
{
    var reminder = ReminderBuilder.AtLocalTime(9, 0)
        .On(DayOfWeek.Monday, DayOfWeek.Wednesday)
        .Build();

    var next = ReminderScheduler.NextOccurrence(
        reminder,
        DateTimeOffset.Parse("2026-09-14T10:00:00Z"),
        TimeZoneInfo.Utc);

    Assert.Equal(DateTimeOffset.Parse("2026-09-16T09:00:00Z"), next);
}

[Fact]
public void Hydration_reconciliation_emits_only_one_occurrence_after_sleep()
{
    var reminder = ReminderBuilder.Every(TimeSpan.FromHours(2))
        .WithMissedPolicy(MissedOccurrencePolicy.LatestOnly)
        .Build();

    var result = ReminderScheduler.Reconcile(
        reminder,
        DateTimeOffset.Parse("2026-09-11T08:00:00Z"),
        DateTimeOffset.Parse("2026-09-11T18:00:00Z"),
        TimeZoneInfo.Utc);

    Assert.Single(result.DueNow);
    Assert.Equal(DateTimeOffset.Parse("2026-09-11T20:00:00Z"), result.NextUtc);
}
```

Add these exact theory cases to the same test file:

```csharp
[Theory]
[InlineData("daily_after_due", "2026-09-12T09:00:00Z")]
[InlineData("quiet_at_2300", "2026-09-12T07:00:00Z")]
[InlineData("spring_forward_0230", "2026-03-08T10:00:00Z")]
[InlineData("snoozed_until_1015", "2026-09-11T10:15:00Z")]
public void Next_occurrence_matches_policy_fixture(string fixtureName, string expectedUtc)
{
    var fixture = ReminderPolicyFixture.Load(fixtureName);
    Assert.Equal(DateTimeOffset.Parse(expectedUtc), fixture.NextOccurrence());
}

[Fact]
public void Expired_one_time_reminder_has_no_next_occurrence()
{
    var fixture = ReminderPolicyFixture.Load("expired_once");
    Assert.Null(fixture.NextOccurrence());
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~ReminderSchedulerTests`  
Expected: FAIL because reminder recurrence types are undefined.

- [ ] **Step 3: Implement recurrence as data and pure calculations**

Define `RecurrenceRule` with exactly four variants: `Once`, `Daily`, `SelectedWeekdays`, and `Interval`. Define `Reminder` with `Id`, `Title`, `Details`, `Enabled`, `Rule`, `LocalTimeZoneId`, `QuietHoursBehavior`, `MissedPolicy`, `NextDueUtc`, and optional `SnoozedUntilUtc`.

The scheduler must:

- choose snooze before recurrence;
- calculate in the reminder's stored Windows time zone;
- advance invalid spring-forward local times to the first valid minute;
- choose the earlier UTC offset for ambiguous fall-back local times;
- defer `WaitUntilQuietHoursEnd` reminders through `QuietHoursPolicy.NextAllowedUtc`;
- emit one latest occurrence for `LatestOnly` and no occurrence for `Skip`;
- never emit a burst of missed interval reminders.

Return immutable `ReminderReconciliation(IReadOnlyList<ReminderOccurrence> DueNow, DateTimeOffset? NextUtc)`.

`ReminderEngine.TickAsync(CancellationToken)` loads reminders due at `IClock.UtcNow`, applies reconciliation, and uses `IReminderRepository.RecordOccurrencesAndAdvanceAsync` to persist occurrences plus next due time in one transaction before invoking `IReminderDueSink.NotifyAsync`. A failed transaction emits no presentation event. The host calls `TickAsync` once at startup/resume and every 30 seconds while running.

- [ ] **Step 4: Run reminder and complete core tests**

Run:

```powershell
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~ReminderSchedulerTests
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj
```

Expected: all reminder cases and the full suite pass.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.Core/Models/Reminder.cs src/Dudu.Core/Reminders src/Dudu.Core/Abstractions/IReminderRepository.cs src/Dudu.Core/Abstractions/IReminderDueSink.cs tests/Dudu.Core.Tests/Reminders
git commit -m "feat: schedule recurring and missed reminders"
```

### Task 5: Implement tasks and restart-safe focus sessions

**Files:**
- Create: `src/Dudu.Core/Models/TaskItem.cs`
- Create: `src/Dudu.Core/Models/FocusSession.cs`
- Create: `src/Dudu.Core/Tasks/TaskService.cs`
- Create: `src/Dudu.Core/Focus/FocusService.cs`
- Create: `src/Dudu.Core/Abstractions/ITaskRepository.cs`
- Create: `src/Dudu.Core/Abstractions/IFocusSessionRepository.cs`
- Create: `tests/Dudu.Core.Tests/Focus/FocusServiceTests.cs`
- Create: `tests/Dudu.Core.Tests/Tasks/TaskServiceTests.cs`

**Interfaces:**
- Consumes: `IClock.UtcNow` and repository interfaces.
- Produces: `TaskService.CreateAsync`, `UpdateAsync`, `CompleteAsync`, `ListActiveAsync`, `FocusService.StartAsync(Guid?, TimeSpan, CancellationToken)`, `PauseAsync`, `ResumeAsync`, `ExtendAsync`, `CompleteExpiredAsync`, `EndAsync`, and `FocusSnapshot`.

- [ ] **Step 1: Write failing tests using in-memory repositories and a fake clock**

```csharp
[Fact]
public async Task Reload_uses_persisted_end_time_instead_of_tick_count()
{
    var clock = new FakeClock("2026-09-11T10:00:00Z");
    var repository = new InMemoryFocusRepository();
    var first = new FocusService(repository, clock);
    var started = await first.StartAsync(null, TimeSpan.FromMinutes(25), TestContext.Current.CancellationToken);

    clock.Advance(TimeSpan.FromMinutes(10));
    var reloaded = new FocusService(repository, clock);
    var snapshot = await reloaded.GetCurrentAsync(TestContext.Current.CancellationToken);

    Assert.Equal(started.Id, snapshot!.Id);
    Assert.Equal(TimeSpan.FromMinutes(15), snapshot.Remaining);
}

[Fact]
public async Task Paused_time_does_not_consume_remaining_duration()
{
    var fixture = FocusFixture.Started(TimeSpan.FromMinutes(25));
    await fixture.Service.PauseAsync(fixture.SessionId, fixture.CancellationToken);
    fixture.Clock.Advance(TimeSpan.FromHours(1));

    var result = await fixture.Service.ResumeAsync(fixture.SessionId, fixture.CancellationToken);

    Assert.Equal(TimeSpan.FromMinutes(25), result.Remaining);
}
```

Add these explicit transition assertions:

```csharp
[Fact]
public async Task Second_active_session_is_rejected() =>
    await FocusAssertions.SecondStartThrowsAsync();

[Fact]
public async Task Extension_moves_end_time_by_requested_duration() =>
    await FocusAssertions.ExtensionMovesEndAsync(TimeSpan.FromMinutes(10));

[Fact]
public async Task Early_end_records_ended_early() =>
    await FocusAssertions.EndRecordsAsync(FocusStatus.EndedEarly);

[Fact]
public async Task Expired_running_session_completes_once() =>
    await FocusAssertions.ExpiryCompletesExactlyOnceAsync();

[Fact]
public async Task Session_preserves_optional_task_id() =>
    await FocusAssertions.TaskAssociationRoundTripsAsync(Guid.Parse("71ad44e7-0ab3-43e4-982e-93f8c8a1d46a"));
```

- [ ] **Step 2: Run the focus tests to verify they fail**

Run: `dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~FocusServiceTests`  
Expected: FAIL because `FocusService` and focus contracts do not exist.

- [ ] **Step 3: Implement persisted focus-state transitions**

Define:

```csharp
public enum FocusStatus { Running, Paused, Completed, EndedEarly }

public sealed record FocusSession(
    Guid Id,
    Guid? TaskId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndsUtc,
    TimeSpan RemainingWhenPaused,
    FocusStatus Status,
    DateTimeOffset UpdatedUtc);

public sealed record FocusSnapshot(
    Guid Id,
    Guid? TaskId,
    FocusStatus Status,
    TimeSpan Remaining);
```

Every command loads the session, validates the transition, computes from `IClock.UtcNow`, and persists before returning. `CompleteExpiredAsync` changes a running session to `Completed` only when `EndsUtc <= UtcNow` and returns `false` otherwise. Use `InvalidOperationException` messages that name the invalid transition.

`TaskService` trims titles, rejects empty or titles over 120 Unicode scalar values, accepts optional notes up to 2,000 scalar values, persists UTC timestamps, and requires an existing incomplete task before associating a new focus session.

- [ ] **Step 4: Run focused tests and the milestone regression suite**

Run:

```powershell
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~FocusServiceTests
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj
```

Expected: focused and complete core suites pass.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.Core/Models src/Dudu.Core/Tasks src/Dudu.Core/Focus src/Dudu.Core/Abstractions tests/Dudu.Core.Tests/Tasks tests/Dudu.Core.Tests/Focus
git commit -m "feat: add durable task focus sessions"
```

### Task 6: Implement local notes, manual check-ins, countdowns, and seasonal outfit policy

**Files:**
- Create: `src/Dudu.Core/Models/LocalLoveNote.cs`
- Create: `src/Dudu.Core/Models/MoodCheckIn.cs`
- Create: `src/Dudu.Core/Models/Countdown.cs`
- Create: `src/Dudu.Core/Models/Outfit.cs`
- Create: `src/Dudu.Core/Notes/LocalNoteSelector.cs`
- Create: `src/Dudu.Core/CheckIns/CheckInService.cs`
- Create: `src/Dudu.Core/Countdowns/CountdownService.cs`
- Create: `src/Dudu.Core/Assets/SeasonalOutfitPolicy.cs`
- Create: `src/Dudu.Core/Abstractions/ILocalNoteRepository.cs`
- Create: `src/Dudu.Core/Abstractions/ICheckInRepository.cs`
- Create: `tests/Dudu.Core.Tests/Companion/CompanionFeatureTests.cs`

**Interfaces:**
- Consumes: `IClock` and `Preferences.LocalNoteDailyLimit`.
- Produces: `LocalNoteSelector.SelectAsync`, `CheckInService.RecordAsync`, `CheckInService.SummarizeAsync`, `CountdownService.GetDisplay`, and `SeasonalOutfitPolicy.Select`.

- [ ] **Step 1: Write failing behavior and privacy tests**

```csharp
[Fact]
public async Task Local_note_selector_respects_daily_cap_and_recent_history()
{
    var fixture = NoteFixture.WithNotes("one", "two", "three");
    await fixture.RecordShownAsync("one", count: 3);

    var result = await fixture.Selector.SelectAsync(manualRequest: false, fixture.CancellationToken);

    Assert.Null(result);
}

[Fact]
public async Task Check_in_summary_never_calls_a_remote_dependency()
{
    var repository = new SpyCheckInRepository();
    var service = new CheckInService(repository, new FakeClock("2026-09-11T10:00:00Z"));
    await service.RecordAsync(MoodChoice.Tired, "long day", TestContext.Current.CancellationToken);

    var summary = await service.SummarizeAsync(7, TestContext.Current.CancellationToken);

    Assert.Equal(1, summary.Counts[MoodChoice.Tired]);
    Assert.Equal(0, repository.RemoteCallCount);
}

[Fact]
public void Automatic_outfit_uses_anniversary_then_base_fallback()
{
    var selected = SeasonalOutfitPolicy.Select(
        new DateOnly(2026, 9, 11),
        new SeasonalDates(Anniversary: new MonthDay(9, 11), Birthday: null),
        availableKeys: ["base", "anniversary"]);

    Assert.Equal("anniversary", selected);
}
```

Add the exact assertions below:

```csharp
[Fact]
public Task Manual_request_bypasses_unsolicited_cap() =>
    CompanionAssertions.ManualRequestReturnsNoteAfterCapAsync();

[Fact]
public Task Selector_excludes_two_most_recent_ids() =>
    CompanionAssertions.RecentIdsAreExcludedAsync(count: 2);

[Fact]
public void Past_countdown_is_zero() =>
    Assert.Equal(TimeSpan.Zero, CompanionFixtures.PastCountdown().Remaining);

[Fact]
public void Missing_seasonal_art_falls_back_to_base() =>
    Assert.Equal("base", SeasonalOutfitPolicy.Select(
        new DateOnly(2026, 12, 25),
        SeasonalDates.Empty,
        ["base"]));
```

- [ ] **Step 2: Run the companion tests to verify they fail**

Run: `dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~CompanionFeatureTests`  
Expected: FAIL because the feature services do not exist.

- [ ] **Step 3: Implement small pure services**

`LocalNoteSelector.SelectAsync(bool manualRequest, CancellationToken)` loads enabled notes, today's unsolicited count, and the two most recent IDs. For unsolicited selection it returns null at the cap; for manual selection it ignores the cap. Select from eligible IDs using an injected `IRandomSource.Next(int)` and persist the shown event transactionally.

`CheckInService` accepts only `Great`, `Okay`, `Tired`, and `Rough`, stores the optional note locally, and returns counts for the requested local-date window. It has no remote interface.

`CountdownService.GetDisplay(Countdown, DateTimeOffset)` returns calendar days for all-day dates and a nonnegative duration for timed events. `SeasonalOutfitPolicy` uses this order: explicit manual outfit, anniversary, birthday, winter dates December 1–31, then `base`; unavailable keys fall back to `base`.

- [ ] **Step 4: Run focused and complete core tests**

Run:

```powershell
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~CompanionFeatureTests
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj
```

Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.Core tests/Dudu.Core.Tests/Companion
git commit -m "feat: add local companion wellbeing features"
```

### Task 7: Create transactional SQLite migrations, repositories, and backup recovery

**Files:**
- Create: `src/Dudu.Infrastructure/Data/Database.cs`
- Create: `src/Dudu.Infrastructure/Data/DatabaseOptions.cs`
- Create: `src/Dudu.Infrastructure/Data/MigrationRunner.cs`
- Create: `src/Dudu.Infrastructure/Data/SeedData.cs`
- Create: `src/Dudu.Infrastructure/Data/Migrations/0001_initial.sql`
- Create: `src/Dudu.Infrastructure/Data/Repositories/ReminderRepository.cs`
- Create: `src/Dudu.Infrastructure/Data/Repositories/TaskRepository.cs`
- Create: `src/Dudu.Infrastructure/Data/Repositories/FocusSessionRepository.cs`
- Create: `src/Dudu.Infrastructure/Data/Repositories/LocalNoteRepository.cs`
- Create: `src/Dudu.Infrastructure/Data/Repositories/CheckInRepository.cs`
- Create: `src/Dudu.Infrastructure/Data/Repositories/CountdownRepository.cs`
- Create: `src/Dudu.Infrastructure/Data/Repositories/PreferencesRepository.cs`
- Create: `src/Dudu.Infrastructure/Data/Repositories/ProfileRepository.cs`
- Create: `src/Dudu.Infrastructure/Data/Repositories/PetPlacementRepository.cs`
- Create: `src/Dudu.Infrastructure/Data/Repositories/RemoteEnvelopeRepository.cs`
- Create: `src/Dudu.Core/Abstractions/ICountdownRepository.cs`
- Create: `src/Dudu.Core/Abstractions/IPreferencesRepository.cs`
- Create: `src/Dudu.Core/Abstractions/IProfileRepository.cs`
- Create: `src/Dudu.Core/Abstractions/IPetPlacementRepository.cs`
- Create: `src/Dudu.Core/Abstractions/IRemoteEnvelopeRepository.cs`
- Create: `src/Dudu.Core/Abstractions/IAppUnitOfWork.cs`
- Create: `src/Dudu.Infrastructure/Data/DatabaseBackupService.cs`
- Create: `tests/Dudu.Infrastructure.Tests/Dudu.Infrastructure.Tests.csproj`
- Create: `tests/Dudu.Infrastructure.Tests/Data/DatabaseTests.cs`

**Interfaces:**
- Consumes: core repository interfaces and UTC model timestamps.
- Produces: `Database.OpenAsync`, concrete repositories, `DatabaseBackupService.CreatePreMigrationBackupAsync`, and `RestoreLatestValidAsync`.

- [ ] **Step 1: Write failing migration, round-trip, rollback, and backup tests**

```csharp
[Fact]
public async Task Migration_creates_all_version_one_tables()
{
    await using var fixture = await DatabaseFixture.CreateAsync();
    var names = await fixture.ReadTableNamesAsync();

    Assert.Contains("schema_version", names);
    Assert.Contains("reminders", names);
    Assert.Contains("focus_sessions", names);
    Assert.Contains("remote_envelopes", names);
    Assert.Contains("processed_remote_messages", names);
    Assert.Contains("mood_check_ins", names);
}

[Fact]
public async Task Failed_migration_rolls_back_and_preserves_original_database()
{
    await using var fixture = await DatabaseFixture.CreateAtVersionAsync(1);
    fixture.AddMigration(2, "CREATE TABLE broken(;"); 

    await Assert.ThrowsAsync<SqliteException>(() => fixture.RunMigrationsAsync());

    Assert.Equal(1, await fixture.ReadSchemaVersionAsync());
    Assert.True(File.Exists(fixture.DatabasePath));
}

[Fact]
public async Task Backup_rotation_keeps_five_newest_valid_backups()
{
    await using var fixture = await DatabaseFixture.CreateAsync();
    await fixture.CreateBackupsAsync(7);

    Assert.Equal(5, Directory.GetFiles(fixture.BackupDirectory, "*.db").Length);
}

[Fact]
public async Task First_database_contains_the_twelve_private_default_notes_once()
{
    await using var fixture = await DatabaseFixture.CreateAsync();
    await fixture.ReopenAsync();

    var notes = await fixture.Services.GetRequiredService<ILocalNoteRepository>()
        .ListEnabledAsync(TestContext.Current.CancellationToken);

    Assert.Equal(12, notes.Count);
    Assert.Equal(12, notes.Select(note => note.Text).Distinct().Count());
}
```

Use this data-driven round-trip test plus the corrupt-backup assertion:

```csharp
[Theory]
[MemberData(nameof(RepositoryRoundTripCases.All), MemberType = typeof(RepositoryRoundTripCases))]
public async Task Repository_round_trips_without_losing_utc_or_optional_fields(
    IRepositoryRoundTripCase testCase)
{
    await using var fixture = await DatabaseFixture.CreateAsync();
    await testCase.AssertRoundTripAsync(fixture.Services, TestContext.Current.CancellationToken);
}

[Fact]
public async Task Restore_rejects_backup_that_fails_integrity_check()
{
    await using var fixture = await DatabaseFixture.CreateAsync();
    var corrupt = await fixture.WriteCorruptBackupAsync();

    var result = await fixture.Backups.TryRestoreAsync(corrupt, TestContext.Current.CancellationToken);

    Assert.False(result.Restored);
    Assert.Equal(RestoreFailure.IntegrityCheckFailed, result.Failure);
    Assert.True(File.Exists(corrupt));
}
```

- [ ] **Step 2: Run infrastructure tests to verify they fail**

Run: `dotnet test tests/Dudu.Infrastructure.Tests/Dudu.Infrastructure.Tests.csproj --filter FullyQualifiedName~DatabaseTests`  
Expected: FAIL because database infrastructure is undefined.

- [ ] **Step 3: Implement schema and explicit SQL repositories**

Use `Microsoft.Data.Sqlite` directly rather than an ORM. `0001_initial.sql` creates all logical entities from the specification, foreign keys, UTC text timestamps in ISO 8601 round-trip format, unique message IDs, and indexes on `next_due_utc`, `deliver_after_utc`, and active-state fields.

`MigrationRunner` must:

1. open with `Foreign Keys=True;Mode=ReadWriteCreate`;
2. run `PRAGMA journal_mode=WAL` and `PRAGMA busy_timeout=5000`;
3. call `CreatePreMigrationBackupAsync` before applying a higher version;
4. apply each SQL resource and update `schema_version` in one transaction;
5. leave both the database and schema version unchanged on failure.

Repositories use parameters for every value. Remote-envelope insertion and processed-ID insertion expose one transaction callback so Task 20 can deduplicate atomically.

`SeedData` inserts these editable local notes exactly once using stable IDs: `You’ve got this 💛`, `I’m proud of you.`, `Take a breath—I’m with you.`, `A little water break for my favorite person?`, `One step at a time.`, `You make ordinary days better.`, `I hope something makes you smile today.`, `Rest is productive too.`, `You’re loved exactly as you are.`, `Sending you a tiny hug.`, `Your best is enough today.`, and `Can’t wait to see you.`

- [ ] **Step 4: Run database tests and both .NET suites**

Run:

```powershell
dotnet test tests/Dudu.Infrastructure.Tests/Dudu.Infrastructure.Tests.csproj
dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj
```

Expected: all tests pass and temporary database directories are deleted by fixtures.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.Infrastructure/Data tests/Dudu.Infrastructure.Tests DuduDesktop.slnx
git commit -m "feat: persist companion data with recoverable SQLite migrations"
```

### Task 8: Protect secrets and wire the headless local application services

**Files:**
- Create: `src/Dudu.Core/Abstractions/ISecretStore.cs`
- Create: `src/Dudu.Core/Abstractions/IPairingService.cs`
- Create: `src/Dudu.Infrastructure/Security/DpapiSecretStore.cs`
- Create: `src/Dudu.Infrastructure/Remote/OfflinePairingService.cs`
- Create: `src/Dudu.Infrastructure/DependencyInjection.cs`
- Create: `src/Dudu.App/Hosting/AppPaths.cs`
- Create: `src/Dudu.App/Hosting/AppHost.cs`
- Create: `tests/Dudu.Infrastructure.Tests/Security/DpapiSecretStoreTests.cs`
- Create: `tests/Dudu.Infrastructure.Tests/DependencyInjectionTests.cs`

**Interfaces:**
- Consumes: all core services and SQLite repositories.
- Produces: `ISecretStore.SetAsync`, `GetAsync`, `DeleteAsync`, `IPairingService`, `AppPaths.ForCurrentUser()`, and `ServiceCollection.AddDuduInfrastructure(DatabaseOptions)`.

- [ ] **Step 1: Write failing DPAPI and composition tests**

```csharp
[Fact]
public async Task Secret_round_trip_is_bound_to_current_user_and_not_plaintext()
{
    using var directory = new TemporaryDirectory();
    var store = new DpapiSecretStore(directory.Path);
    var secret = Encoding.UTF8.GetBytes("desktop-capability-token");

    await store.SetAsync("relay-token", secret, TestContext.Current.CancellationToken);

    Assert.DoesNotContain("desktop-capability-token", await File.ReadAllTextAsync(
        Path.Combine(directory.Path, "relay-token.bin"),
        TestContext.Current.CancellationToken));
    Assert.Equal(secret, await store.GetAsync("relay-token", TestContext.Current.CancellationToken));
}

[Fact]
public void Infrastructure_registration_resolves_every_repository_once()
{
    using var provider = ServiceFixture.Build();

    Assert.IsType<ReminderRepository>(provider.GetRequiredService<IReminderRepository>());
    Assert.Same(
        provider.GetRequiredService<IFocusSessionRepository>(),
        provider.GetRequiredService<IFocusSessionRepository>());
}
```

- [ ] **Step 2: Run focused tests to verify they fail**

Run: `dotnet test tests/Dudu.Infrastructure.Tests/Dudu.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DpapiSecretStoreTests|FullyQualifiedName~DependencyInjectionTests"`  
Expected: FAIL because `DpapiSecretStore` and `AddDuduInfrastructure` do not exist.

- [ ] **Step 3: Implement user-scoped protection and service composition**

`DpapiSecretStore` validates keys with `^[a-z0-9-]{1,64}$`, protects bytes with `DataProtectionScope.CurrentUser` and entropy `UTF8("DuduDesktop:v1:" + key)`, writes through a temporary file followed by atomic replacement, and zeroes plaintext buffers after use. Missing keys return null; corrupt protected data raises `SecretStoreException` without deleting the file.

`AppPaths.ForCurrentUser()` resolves:

```csharp
var root = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "DuduDesktop");
return new AppPaths(
    Root: root,
    Database: Path.Combine(root, "dudu.db"),
    Backups: Path.Combine(root, "backups"),
    Secrets: Path.Combine(root, "secrets"),
    Logs: Path.Combine(root, "logs"));
```

`IPairingService` exposes `GetStateAsync`, `CreateCodeAsync`, `DisconnectSenderSessionsAsync`, and `DeleteRemoteDeviceAsync`. Register `OfflinePairingService` initially; it returns `PairingAvailability.Offline` without throwing so onboarding and settings compile before the relay implementation replaces it in Task 20.

`AddDuduInfrastructure` registers one `Database`, singleton repositories, core services, `IClock`, `ISecretStore`, and the temporary `OfflinePairingService`. `AppHost.StartAsync` creates directories, opens/migrates the database, and starts schedulers only after successful migration. `StopAsync` cancels background work and disposes services within five seconds.

- [ ] **Step 4: Run all local-engine tests**

Run:

```powershell
dotnet test DuduDesktop.slnx -c Release --no-restore
```

Expected: all core and infrastructure tests pass. On Windows, the DPAPI test passes; on non-Windows agents it is reported skipped with the explicit reason `Windows DPAPI required`.

- [ ] **Step 5: Commit milestone 1**

```bash
git add src tests DuduDesktop.slnx
git commit -m "feat: compose secure local companion services"
```

## Milestone 2 — Windows pet

### Task 9: Build the validated asset manifest and deterministic private import tool

**Files:**
- Create: `src/Dudu.Core/Assets/AssetManifest.cs`
- Create: `src/Dudu.App/Animation/AssetManifestLoader.cs`
- Create: `tools/Dudu.AssetTool/Dudu.AssetTool.csproj`
- Create: `tools/Dudu.AssetTool/Program.cs`
- Create: `tools/Dudu.AssetTool/AssetNormalizer.cs`
- Create: `assets/sources/private-dudu-sources.json`
- Create: `assets/raw/.gitkeep`
- Create: `src/Dudu.App/Assets/Packs/fallback/manifest.json`
- Create: `src/Dudu.App/Assets/Packs/fallback/idle.png`
- Create: `tests/Dudu.App.Tests/Dudu.App.Tests.csproj`
- Create: `tests/Dudu.App.Tests/Animation/AssetManifestLoaderTests.cs`
- Create: `tools/Dudu.AssetTool.Tests/AssetNormalizerTests.cs`

**Interfaces:**
- Consumes: `SeasonalOutfitPolicy` and `ProductInfo.ProtocolVersion`.
- Produces: `AssetManifestLoader.LoadAsync(string, CancellationToken): AssetPack` and CLI commands `import`, `normalize`, and `validate`.

- [ ] **Step 1: Write failing manifest and normalization tests**

```csharp
[Fact]
public async Task Loader_rejects_animation_without_reduced_motion_pose()
{
    var path = FixtureManifest.Write(animationKey: "comfort-hug", reducedMotionKey: null);

    var exception = await Assert.ThrowsAsync<AssetManifestException>(
        () => AssetManifestLoader.LoadAsync(path, TestContext.Current.CancellationToken));

    Assert.Contains("reducedMotion", exception.Message);
}

[Fact]
public void Normalizer_keeps_a_shared_anchor_across_trimmed_frames()
{
    using var frames = TestFrames.TwoWithDifferentTransparentBounds();

    var result = AssetNormalizer.Normalize(frames, new PixelPoint(50, 100), 512);

    Assert.All(result.Frames, frame => Assert.Equal(result.Anchor, frame.Anchor));
    Assert.All(result.Frames, frame => Assert.Equal(512, frame.CanvasSize));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj --filter FullyQualifiedName~AssetManifestLoaderTests
dotnet test tools/Dudu.AssetTool.Tests/Dudu.AssetTool.Tests.csproj --filter FullyQualifiedName~AssetNormalizerTests
```

Expected: both commands fail because loaders and normalization types do not exist.

- [ ] **Step 3: Implement manifest version 1 and source accountability**

The manifest schema contains `schemaVersion`, `packId`, `version`, `privateUseOnly`, `attribution`, `outfits`, and animations keyed by `idle`, `blink`, `greeting`, `sleep`, `drink`, `focus`, `celebrate`, `comfort-hug`, and `note-arrival`. Every animation contains ordered PNG frames, positive millisecond durations, loop mode, anchor, nominal size, and a reduced-motion pose.

The tool:

- verifies source SHA-256 before transforming;
- stores no downloaded file outside `assets/raw`;
- trims transparent bounds while preserving one pack anchor;
- resizes to a maximum 512×512 canvas without upscaling;
- converts to premultiplied BGRA PNG;
- emits deterministic filenames and output hashes;
- writes transformation details back to `private-dudu-sources.json`.

Create the fallback pose as an original simple neutral bear silhouette rather than copied Dudu artwork. Populate source records with `privateUseOnly: true`, `accessedUtc: 2026-09-11T00:00:00Z`, creator attribution `Huang Xiao B` where known, and these exact discovery URLs:

- `https://huangxiaob.com/`
- `https://tenor.com/view/dudu-cute-smile-photo-gif-3117522263724372729`
- `https://tenor.com/view/bubu-dudu-gif-25349847`
- `https://tenor.com/view/dudu-waiting-dudu-cute-in-love-gif-14672583372493863103`
- `https://tenor.com/view/bubu-dudu-gif-13961388528266603475`
- `https://getstickerpack.com/stickers/xiao-xiong-yi-er-he-bu-bu-credits-to-huang-xiaob`

- [ ] **Step 4: Normalize collected assets and validate both packs**

Run:

```powershell
dotnet run --project tools/Dudu.AssetTool -- import --sources assets/sources/private-dudu-sources.json --output assets/raw --record-checksums
dotnet run --project tools/Dudu.AssetTool -- normalize --sources assets/sources/private-dudu-sources.json --input assets/raw --output src/Dudu.App/Assets/Packs/private-dudu
dotnet run --project tools/Dudu.AssetTool -- validate src/Dudu.App/Assets/Packs/private-dudu/manifest.json
dotnet run --project tools/Dudu.AssetTool -- validate src/Dudu.App/Assets/Packs/fallback/manifest.json
dotnet test tools/Dudu.AssetTool.Tests/Dudu.AssetTool.Tests.csproj
dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj
```

Expected: both manifests validate, all source checksums match, all tests pass, and no frame exceeds 512×512.

- [ ] **Step 5: Commit the private asset pack locally**

```bash
git add tools assets/sources assets/raw/.gitkeep src/Dudu.Core/Assets src/Dudu.App/Animation src/Dudu.App/Assets tests/Dudu.App.Tests DuduDesktop.slnx
git commit -m "feat: add manifest-driven private Dudu assets"
```

Keep the repository private. Do not push the artwork to a public remote.

### Task 10: Implement allocation-bounded animation scheduling and Skia composition

**Files:**
- Create: `src/Dudu.App/Animation/IFramePresenter.cs`
- Create: `src/Dudu.App/Animation/AnimationEngine.cs`
- Create: `src/Dudu.App/Animation/RenderedFrame.cs`
- Create: `src/Dudu.App/Animation/SkiaFrameComposer.cs`
- Create: `tests/Dudu.App.Tests/Animation/AnimationEngineTests.cs`
- Create: `tests/Dudu.App.Tests/Animation/SkiaFrameComposerTests.cs`

**Interfaces:**
- Consumes: `AssetPack` and `PetPresentation.AnimationKey`.
- Produces: `AnimationEngine.PlayAsync(PetPresentation, AnimationOptions, CancellationToken)`, `IFramePresenter.PresentAsync(RenderedFrame, CancellationToken)`, and `RenderedFrame` with premultiplied BGRA bytes.

- [ ] **Step 1: Write failing timing, fallback, and allocation tests**

```csharp
[Fact]
public async Task One_shot_animation_preserves_semantic_duration_when_frames_are_late()
{
    var clock = new ManualAnimationClock();
    var presenter = new RecordingFramePresenter(clock);
    var engine = AnimationFixture.Create(presenter, clock, durations: [100, 100, 100]);

    var play = engine.PlayAsync(TestPresentation("celebrate"), AnimationOptions.Default, CancellationToken.None);
    clock.AdvanceBy(250);
    clock.AdvanceBy(50);
    await play;

    Assert.Equal(TimeSpan.FromMilliseconds(300), presenter.SemanticDuration);
    Assert.InRange(presenter.Frames.Count, 2, 3);
}

[Fact]
public async Task Missing_animation_uses_fallback_idle_pose()
{
    var fixture = AnimationFixture.WithMissingKey("unknown");

    await fixture.Engine.PlayAsync(TestPresentation("unknown"), AnimationOptions.ReducedMotion, CancellationToken.None);

    Assert.Equal("fallback/idle.png", fixture.Presenter.Single.Source);
}
```

Add this allocation assertion after warming the cache:

```csharp
[Fact]
public async Task Repeated_cached_frames_allocate_less_than_fifty_kib()
{
    var fixture = AnimationFixture.WithCachedIdleFrame();
    await fixture.WarmAsync();
    var before = GC.GetAllocatedBytesForCurrentThread();

    await fixture.PresentFramesAsync(300);

    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Assert.InRange(allocated, 0, 50 * 1024);
}
```

- [ ] **Step 2: Run animation tests to verify they fail**

Run: `dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj --filter "FullyQualifiedName~AnimationEngineTests|FullyQualifiedName~SkiaFrameComposerTests"`  
Expected: FAIL because animation engine types are undefined.

- [ ] **Step 3: Implement frame caching, monotonic timing, and reduced motion**

Load decoded `SKBitmap` instances once per selected pack. Compose into one reusable premultiplied BGRA buffer. Use `Stopwatch.GetTimestamp` for frame deadlines, skip already-expired frames, and preserve one-shot completion time. Looping animations stop only on cancellation or replacement.

`AnimationOptions` contains `Scale`, `ReducedMotion`, and `OutfitKey`. Reduced motion presents the manifest fallback pose for at least the semantic animation duration with an optional 120 ms opacity fade. Resolve frames in this order: selected outfit animation, selected outfit idle, base matching animation, fallback idle.

Dispose decoded images and pooled buffers when changing packs or shutting down.

- [ ] **Step 4: Run app tests in Release**

Run:

```powershell
dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj -c Release
```

Expected: timing, fallback, composition, and allocation tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.App/Animation tests/Dudu.App.Tests/Animation
git commit -m "feat: render efficient manifest animations"
```

### Task 11: Create the no-activate layered pet window, alpha hit testing, and DPI-safe placement

**Files:**
- Create: `src/Dudu.App/Overlay/NativeMethods.txt`
- Create: `src/Dudu.App/Overlay/OverlayWindowHost.cs`
- Create: `src/Dudu.App/Overlay/OverlayHitTest.cs`
- Create: `src/Dudu.App/Overlay/MonitorPlacementService.cs`
- Create: `src/Dudu.App/Animation/LayeredFramePresenter.cs`
- Create: `tests/Dudu.App.Tests/Overlay/OverlayHitTestTests.cs`
- Create: `tests/Dudu.App.Tests/Overlay/MonitorPlacementServiceTests.cs`
- Create: `tests/Dudu.WindowsHarness/Dudu.WindowsHarness.csproj`
- Create: `tests/Dudu.WindowsHarness/Program.cs`

**Interfaces:**
- Consumes: `IFramePresenter`, `RenderedFrame`, and `PetPlacement`.
- Produces: `OverlayWindowHost.CreateAsync`, `Show`, `Hide`, `SetPlacement`, `OverlayHitTest.IsInteractive`, and `MonitorPlacementService.Resolve`.

- [ ] **Step 1: Write failing pure tests for transparent pixels and missing monitors**

```csharp
[Theory]
[InlineData(0, false)]
[InlineData(7, false)]
[InlineData(8, true)]
[InlineData(255, true)]
public void Alpha_hit_test_uses_eight_as_the_interactive_threshold(byte alpha, bool expected)
{
    Assert.Equal(expected, OverlayHitTest.IsInteractive(alpha));
}

[Fact]
public void Missing_saved_monitor_moves_pet_into_primary_work_area()
{
    var monitors = new[]
    {
        new MonitorInfo("DISPLAY1", new PixelRect(0, 0, 1920, 1040), 144, IsPrimary: true)
    };
    var saved = new PetPlacement("REMOVED", 0.95, 0.95, 1.0);

    var result = MonitorPlacementService.Resolve(saved, new PixelSize(512, 512), monitors);

    Assert.Equal("DISPLAY1", result.MonitorDeviceName);
    Assert.True(monitors[0].WorkArea.Contains(result.WindowBounds));
}

[Fact]
public void Scale_is_clamped_between_half_and_double_size()
{
    Assert.Equal(0.5, MonitorPlacementService.ClampScale(0.1));
    Assert.Equal(2.0, MonitorPlacementService.ClampScale(4.0));
}
```

- [ ] **Step 2: Run overlay tests to verify they fail**

Run: `dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj --filter "FullyQualifiedName~OverlayHitTestTests|FullyQualifiedName~MonitorPlacementServiceTests"`  
Expected: FAIL because overlay and placement types do not exist.

- [ ] **Step 3: Generate only required Win32 bindings and create the layered HWND**

Put these symbols in `NativeMethods.txt`:

```text
RegisterClassEx
CreateWindowEx
DestroyWindow
DefWindowProc
ShowWindow
SetWindowPos
UpdateLayeredWindow
GetDC
ReleaseDC
MonitorFromWindow
GetMonitorInfo
EnumDisplayMonitors
GetDpiForWindow
SetProcessDpiAwarenessContext
SetForegroundWindow
TrackMouseEvent
SetCapture
ReleaseCapture
CreateCompatibleDC
DeleteDC
CreateDIBSection
SelectObject
DeleteObject
PostMessage
WM_NCHITTEST
WM_MOUSEACTIVATE
WM_DPICHANGED
WM_DISPLAYCHANGE
WM_LBUTTONDOWN
WM_LBUTTONUP
WM_LBUTTONDBLCLK
WM_RBUTTONUP
WM_MOUSEMOVE
WM_MOUSEWHEEL
WS_EX_LAYERED
WS_EX_TOOLWINDOW
WS_EX_NOACTIVATE
ULW_ALPHA
```

Create the window with `WS_POPUP` and extended styles `WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`. Return `MA_NOACTIVATE` from `WM_MOUSEACTIVATE`. Return `HTTRANSPARENT` for pixels below alpha 8 and `HTCLIENT` for visible pixels or explicit bubble hit regions. Use pointer capture only while dragging. Route `WM_LBUTTONDBLCLK` to `OpenHome` and `WM_RBUTTONUP` to the tray-style context menu without activating the overlay.

`LayeredFramePresenter` pins the reusable premultiplied BGRA buffer, creates a compatible bitmap, and calls `UpdateLayeredWindow` with `AC_SRC_ALPHA`. It releases every GDI handle in `finally` blocks. `MonitorPlacementService` stores normalized work-area coordinates and handles `WM_DPICHANGED` and `WM_DISPLAYCHANGE`.

- [ ] **Step 4: Verify tests and run the visual harness**

Run:

```powershell
dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj --filter "FullyQualifiedName~Overlay"
dotnet run --project tests/Dudu.WindowsHarness -- --scenario layered-window
```

Expected: tests pass. The harness displays a transparent test pet; clicking transparent corners activates the underlying Notepad window, dragging visible pixels moves the pet, mouse-wheel input changes scale from 0.5× to 2.0×, and typing continues in Notepad throughout.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.App/Overlay src/Dudu.App/Animation/LayeredFramePresenter.cs tests/Dudu.App.Tests/Overlay tests/Dudu.WindowsHarness DuduDesktop.slnx
git commit -m "feat: add transparent no-activate pet overlay"
```

### Task 12: Add single-instance hosting, tray controls, pause policy, fullscreen suppression, and startup registration

**Files:**
- Create: `src/Dudu.App/Hosting/SingleInstanceCoordinator.cs`
- Create: `src/Dudu.App/Hosting/AppLifecycleCoordinator.cs`
- Create: `src/Dudu.App/Tray/TrayIconService.cs`
- Create: `src/Dudu.App/System/FullscreenDetector.cs`
- Create: `src/Dudu.App/System/PausePolicy.cs`
- Create: `src/Dudu.App/System/StartupRegistrationService.cs`
- Create: `src/Dudu.App/System/GlobalHotkeyService.cs`
- Modify: `src/Dudu.App/Hosting/AppHost.cs`
- Modify: `src/Dudu.App/Overlay/NativeMethods.txt`
- Create: `tests/Dudu.App.Tests/System/FullscreenDetectorTests.cs`
- Create: `tests/Dudu.App.Tests/System/PausePolicyTests.cs`
- Create: `tests/Dudu.App.Tests/System/GlobalHotkeyServiceTests.cs`
- Create: `tests/Dudu.App.Tests/Hosting/SingleInstanceCoordinatorTests.cs`

**Interfaces:**
- Consumes: `AppHost`, `OverlayWindowHost`, `Preferences`, and `PetStateMachine`.
- Produces: `SingleInstanceCoordinator.TryAcquireAsync`, `ActivatePrimaryAsync`, `FullscreenDetector.IsForegroundFullscreen`, `PausePolicy.IsSuppressed`, `TrayIconService` commands, `GlobalHotkeyService.SetGesture`, and `StartupRegistrationService.SetEnabledAsync`.

- [ ] **Step 1: Write failing policy and single-instance tests**

```csharp
[Theory]
[InlineData(PauseMode.None, false, false)]
[InlineData(PauseMode.OneHour, true, true)]
[InlineData(PauseMode.UntilFullscreenEnds, true, true)]
[InlineData(PauseMode.UntilFullscreenEnds, false, false)]
[InlineData(PauseMode.Indefinite, false, true)]
public void Pause_policy_combines_mode_expiry_and_fullscreen(
    PauseMode mode, bool fullscreen, bool expected)
{
    var now = DateTimeOffset.Parse("2026-09-11T10:00:00Z");
    var state = new PauseState(mode, now.AddMinutes(30));

    Assert.Equal(expected, PausePolicy.IsSuppressed(state, now, fullscreen));
}

[Fact]
public async Task Second_instance_sends_activation_and_exits()
{
    await using var primary = await SingleInstanceFixture.StartPrimaryAsync();
    await using var secondary = SingleInstanceFixture.CreateSecondary();

    var acquired = await secondary.TryAcquireAsync(TestContext.Current.CancellationToken);

    Assert.False(acquired);
    Assert.Equal(AppActivation.OpenHome, await primary.NextActivationAsync());
}

[Fact]
public void Conflicting_hotkey_preserves_the_previous_registration()
{
    var native = new FakeHotkeyNativeApi();
    var service = new GlobalHotkeyService(native);
    service.SetGesture(HotkeyGesture.Parse("Ctrl+Alt+D"));
    native.RejectNextRegistration();

    Assert.Throws<HotkeyConflictException>(() =>
        service.SetGesture(HotkeyGesture.Parse("Ctrl+Shift+D")));
    Assert.Equal("Ctrl+Alt+D", service.CurrentGesture.ToString());
}
```

Write `FullscreenDetectorTests` against rectangle inputs: foreground equals monitor bounds returns true; foreground equals work area returns false; cloaked, minimized, shell, and overlay HWNDs return false.

- [ ] **Step 2: Run system tests to verify they fail**

Run: `dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj --filter "FullyQualifiedName~PausePolicyTests|FullyQualifiedName~FullscreenDetectorTests|FullyQualifiedName~GlobalHotkeyServiceTests|FullyQualifiedName~SingleInstanceCoordinatorTests"`  
Expected: FAIL because lifecycle types are undefined.

- [ ] **Step 3: Implement Windows lifecycle services**

Use a current-user named mutex `Local\DuduDesktop.App.v1` and named pipe `DuduDesktop.Activation.v1.<user-sid-hash>`. The primary listens for a one-byte `AppActivation` value; a secondary connects with a two-second timeout, writes `OpenHome`, and exits.

Add `GetForegroundWindow`, `GetShellWindow`, `IsIconic`, `DwmGetWindowAttribute`, `RegisterHotKey`, and `UnregisterHotKey` to `NativeMethods.txt`. `FullscreenDetector` compares the foreground window's extended frame bounds to its nearest monitor bounds with a two-pixel tolerance. It excludes the shell, desktop manager, cloaked/minimized windows, and Dudu-owned HWNDs.

Create the tray icon with `Shell_NotifyIcon` and menu commands:

- Show or hide Dudu
- Pause for one hour
- Pause until tomorrow at 07:00 local time
- Pause until fullscreen ends
- Pause indefinitely or resume
- Open settings
- Exit

`StartupRegistrationService` creates or removes a current-user `.lnk` in `Environment.SpecialFolder.Startup` targeting the installed executable with `--background`. `AppLifecycleCoordinator` handles display change, session lock/unlock, suspend/resume, and recreates the tray icon after `TaskbarCreated`.

`GlobalHotkeyService` defaults to `Ctrl+Alt+D`, validates one modifier plus one non-modifier key, registers through `RegisterHotKey`, and preserves the prior working gesture if a requested gesture conflicts. Unlock/resume sends `PetEvent.WelcomeBack` only when quiet hours, pause, and fullscreen gates permit it.

Add `Preferences.HidePetDuringFullscreen` with a default of `true`. When fullscreen starts, hide the overlay as well as suppressing unsolicited presentations; when it ends, restore Dudu at the saved placement without producing a welcome-back event. The tray and global hotkey remain available.

- [ ] **Step 4: Run tests and a two-instance harness check**

Run:

```powershell
dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj --filter "FullyQualifiedName~System|FullyQualifiedName~Hosting"
dotnet run --project tests/Dudu.WindowsHarness -- --scenario single-instance
```

Expected: tests pass. The harness starts two processes, observes one overlay, and records exactly one `OpenHome` activation in the primary.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.App/Hosting src/Dudu.App/Tray src/Dudu.App/System src/Dudu.App/Overlay/NativeMethods.txt tests/Dudu.App.Tests tests/Dudu.WindowsHarness
git commit -m "feat: manage Windows companion lifecycle and tray"
```

### Task 13: Build the modern WinUI shell and two-minute onboarding

**Files:**
- Create: `src/Dudu.App/App.xaml`
- Create: `src/Dudu.App/App.xaml.cs`
- Create: `src/Dudu.App/Windows/SettingsWindow.xaml`
- Create: `src/Dudu.App/Windows/SettingsWindow.xaml.cs`
- Create: `src/Dudu.App/Pages/OnboardingPage.xaml`
- Create: `src/Dudu.App/Pages/OnboardingPage.xaml.cs`
- Create: `src/Dudu.App/ViewModels/SettingsShellViewModel.cs`
- Create: `src/Dudu.App/ViewModels/OnboardingViewModel.cs`
- Create: `src/Dudu.App/Themes/Colors.xaml`
- Create: `src/Dudu.App/Themes/Controls.xaml`
- Create: `tests/Dudu.App.Tests/ViewModels/OnboardingViewModelTests.cs`
- Create: `tests/Dudu.UiTests/Dudu.UiTests.csproj`
- Create: `tests/Dudu.UiTests/OnboardingTests.cs`

**Interfaces:**
- Consumes: `PreferencesRepository`, `StartupRegistrationService`, `MonitorPlacementService`, and `IPairingService`.
- Produces: `OnboardingViewModel.NextAsync`, `Back`, `CompleteAsync`, `SettingsShellViewModel.Navigate`, and stable automation IDs for every onboarding action.

- [ ] **Step 1: Write failing view-model tests for defaults, skipping pairing, and atomic completion**

```csharp
[Fact]
public async Task Recommended_defaults_create_a_quiet_low_interruption_profile()
{
    var fixture = OnboardingFixture.Create();

    await fixture.ViewModel.AcceptRecommendedDefaultsAsync(fixture.CancellationToken);

    Assert.Equal(new TimeOnly(22, 0), fixture.ViewModel.QuietHoursStart);
    Assert.Equal(new TimeOnly(7, 0), fixture.ViewModel.QuietHoursEnd);
    Assert.Equal(3, fixture.ViewModel.LocalNoteDailyLimit);
    Assert.True(fixture.ViewModel.HidePetDuringFullscreen);
}

[Fact]
public async Task Completion_writes_profile_and_preferences_in_one_transaction()
{
    var fixture = OnboardingFixture.Create();
    fixture.ViewModel.RecipientName = "Mia";
    fixture.ViewModel.SkipPairing();

    await fixture.ViewModel.CompleteAsync(fixture.CancellationToken);

    Assert.Equal("Mia", fixture.SavedProfile!.RecipientName);
    Assert.True(fixture.SavedProfile.OnboardingComplete);
    Assert.Equal(1, fixture.UnitOfWork.CommitCount);
}
```

- [ ] **Step 2: Run view-model tests to verify they fail**

Run: `dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj --filter FullyQualifiedName~OnboardingViewModelTests`  
Expected: FAIL because onboarding view-model types do not exist.

- [ ] **Step 3: Implement WinUI resources, shell, and onboarding states**

Use a Mica backdrop, custom title bar, Segoe UI Variable, 8 px spacing multiples, 12 px card corners, cream/blush/lavender semantic brushes, and system high-contrast resources. Build a `NavigationView` containing exactly Home, Reminders, Tasks and Focus, Love Notes, Appearance, Connection, and Privacy and Data.

Onboarding has six steps with progress text and automation IDs:

```xml
<NavigationView x:Name="RootNavigation"
                IsBackButtonVisible="Collapsed"
                IsSettingsVisible="False"
                PaneDisplayMode="LeftCompact">
    <NavigationView.MenuItems>
        <NavigationViewItem Content="Home" Tag="home" AutomationProperties.AutomationId="NavHome" />
        <NavigationViewItem Content="Reminders" Tag="reminders" AutomationProperties.AutomationId="NavReminders" />
        <NavigationViewItem Content="Tasks and Focus" Tag="tasks" AutomationProperties.AutomationId="NavTasksFocus" />
        <NavigationViewItem Content="Love Notes" Tag="notes" AutomationProperties.AutomationId="NavLoveNotes" />
        <NavigationViewItem Content="Appearance" Tag="appearance" AutomationProperties.AutomationId="NavAppearance" />
        <NavigationViewItem Content="Connection" Tag="connection" AutomationProperties.AutomationId="NavConnection" />
        <NavigationViewItem Content="Privacy and Data" Tag="privacy" AutomationProperties.AutomationId="NavPrivacy" />
    </NavigationView.MenuItems>
</NavigationView>
```

Steps collect recipient name, theme/reduced motion, quiet hours, reminder defaults, live pet placement, startup preference, and optional pairing. Pairing has an explicit `Skip for now` button. Persist only on final completion through one unit of work.

On first launch show onboarding. On later launches, start Dudu and the tray icon without opening Settings when `--background` is present; normal Start menu launch opens Home. Tray `Open settings` and pet double-click always open the existing Settings window.

- [ ] **Step 4: Run unit tests and onboarding UI automation**

Run:

```powershell
dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj --filter FullyQualifiedName~Onboarding
dotnet publish src/Dudu.App/Dudu.App.csproj -c Debug -r win-x64
dotnet test tests/Dudu.UiTests/Dudu.UiTests.csproj --filter FullyQualifiedName~OnboardingTests
```

Expected: tests pass. FlaUI completes defaults, skips pairing, places the pet, and reaches Home in fewer than 20 UI actions without keyboard-focus loss to the overlay.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.App/App.xaml* src/Dudu.App/Windows src/Dudu.App/Pages/OnboardingPage.xaml* src/Dudu.App/ViewModels src/Dudu.App/Themes tests/Dudu.App.Tests/ViewModels tests/Dudu.UiTests DuduDesktop.slnx
git commit -m "feat: add modern settings shell and onboarding"
```

### Task 14: Implement the seven settings pages and interactive pet action bubble

**Files:**
- Create: `src/Dudu.App/Pages/HomePage.xaml`
- Create: `src/Dudu.App/Pages/HomePage.xaml.cs`
- Create: `src/Dudu.App/Pages/RemindersPage.xaml`
- Create: `src/Dudu.App/Pages/RemindersPage.xaml.cs`
- Create: `src/Dudu.App/Pages/TasksFocusPage.xaml`
- Create: `src/Dudu.App/Pages/TasksFocusPage.xaml.cs`
- Create: `src/Dudu.App/Pages/LoveNotesPage.xaml`
- Create: `src/Dudu.App/Pages/LoveNotesPage.xaml.cs`
- Create: `src/Dudu.App/Pages/AppearancePage.xaml`
- Create: `src/Dudu.App/Pages/AppearancePage.xaml.cs`
- Create: `src/Dudu.App/Pages/ConnectionPage.xaml`
- Create: `src/Dudu.App/Pages/ConnectionPage.xaml.cs`
- Create: `src/Dudu.App/Pages/PrivacyDataPage.xaml`
- Create: `src/Dudu.App/Pages/PrivacyDataPage.xaml.cs`
- Create: `src/Dudu.App/ViewModels/HomeViewModel.cs`
- Create: `src/Dudu.App/ViewModels/RemindersViewModel.cs`
- Create: `src/Dudu.App/ViewModels/TasksFocusViewModel.cs`
- Create: `src/Dudu.App/ViewModels/LoveNotesViewModel.cs`
- Create: `src/Dudu.App/ViewModels/AppearanceViewModel.cs`
- Create: `src/Dudu.App/ViewModels/ConnectionViewModel.cs`
- Create: `src/Dudu.App/ViewModels/PrivacyDataViewModel.cs`
- Create: `src/Dudu.App/Overlay/ActionBubbleLayout.cs`
- Create: `src/Dudu.App/Overlay/OverlayCommandRouter.cs`
- Create: `tests/Dudu.App.Tests/ViewModels/FeatureViewModelTests.cs`
- Create: `tests/Dudu.App.Tests/Overlay/ActionBubbleLayoutTests.cs`
- Create: `tests/Dudu.UiTests/SettingsNavigationTests.cs`

**Interfaces:**
- Consumes: all local feature services, `PetStateMachine`, `AnimationEngine`, and pairing state.
- Produces: page view-model commands, `ActionBubbleLayout.Arrange`, and `OverlayCommandRouter.ExecuteAsync(OverlayAction, CancellationToken)`.

- [ ] **Step 1: Write failing command and layout tests**

```csharp
[Fact]
public async Task Completing_a_reminder_persists_before_dismissing_pet_state()
{
    var fixture = FeatureFixture.WithDueReminder();

    await fixture.Reminders.CompleteCommand.ExecuteAsync(fixture.Reminder);

    Assert.Equal(new[] { "repository.complete", "pet.dismiss" }, fixture.Events);
}

[Fact]
public void Action_bubble_never_exposes_more_than_six_primary_actions()
{
    var actions = Enum.GetValues<OverlayAction>();

    var layout = ActionBubbleLayout.Arrange(actions, new PixelRect(0, 0, 1920, 1040), new PixelPoint(1800, 900));

    Assert.InRange(layout.PrimaryActions.Count, 1, 6);
    Assert.True(new PixelRect(0, 0, 1920, 1040).Contains(layout.Bounds));
}

[Fact]
public async Task Saving_a_remote_note_to_jar_is_explicit()
{
    var fixture = LoveNotesFixture.WithOpenedRemoteNote("You can do it");

    Assert.Empty(fixture.LocalNotes);
    await fixture.ViewModel.SaveOpenedNoteCommand.ExecuteAsync(null);

    Assert.Equal("You can do it", Assert.Single(fixture.LocalNotes).Text);
}
```

- [ ] **Step 2: Run page and overlay tests to verify they fail**

Run: `dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj --filter "FullyQualifiedName~FeatureViewModelTests|FullyQualifiedName~ActionBubbleLayoutTests"`  
Expected: FAIL because page view models and action-bubble types do not exist.

- [ ] **Step 3: Implement focused page functions and overlay commands**

Home shows pet/pause state, next reminder, active focus, countdown creation/editing, quick actions, and optional manual mood check-in/history. Reminders supports list, create/edit, daily/weekday/weekly-equivalent/interval recurrence, snooze, hydration defaults, and quiet-hours behavior. Tasks and Focus supports task CRUD, completion, focus presets 15/25/45/60 minutes, custom duration, pause, extend, and end.

Love Notes supports local-note CRUD, the three-per-day default, unopened remote-envelope count, explicit reveal, and explicit save-to-jar. Appearance controls theme, reduced motion, pet scale, monitor, outfit, automatic seasonal mode, always-on-top, hide-during-fullscreen, and the global show/hide shortcut. Connection displays pairing state, creates a ten-minute code, lists session count without identifying browser history, and revokes sessions. Privacy and Data explains every stored field and provides backup, restore, local deletion, and remote-device deletion.

The layered action bubble renders within the same no-activate HWND. It shows at most six context actions selected from `Pet`, `Drink water`, `Start focus`, `Tasks`, `Love note`, and `Comfort me`. Each visible action has a pixel hit region and an equivalent tray/settings path.

`Comfort me` opens a calm panel with exactly `Breathe with me`, `Tiny hug`, `Read a love note`, `Take a five-minute break`, and `Close`. Breathing is a user-started 60-second local visual cycle, cancellable at any time, with a static text alternative under reduced motion. None of these actions record or infer a mood.

- [ ] **Step 4: Run page tests and settings navigation automation**

Run:

```powershell
dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj --filter "FullyQualifiedName~ViewModels|FullyQualifiedName~ActionBubble"
dotnet test tests/Dudu.UiTests/Dudu.UiTests.csproj --filter FullyQualifiedName~SettingsNavigationTests
```

Expected: all tests pass. UI automation visits all seven pages, creates and completes one reminder and task, starts and ends a focus session, records a local check-in, changes outfit, and finds every actionable control by automation ID.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.App/Pages src/Dudu.App/ViewModels src/Dudu.App/Overlay tests/Dudu.App.Tests tests/Dudu.UiTests
git commit -m "feat: complete companion settings and pet actions"
```

### Task 15: Add private notifications and enforce suppression at one presentation gateway

**Files:**
- Create: `src/Dudu.Core/Abstractions/INotificationService.cs`
- Create: `src/Dudu.App/Notifications/AppNotificationService.cs`
- Create: `src/Dudu.App/Presentation/PresentationPolicy.cs`
- Create: `src/Dudu.App/Presentation/PresentationCoordinator.cs`
- Create: `src/Dudu.App/Presentation/ReminderDueSink.cs`
- Create: `tests/Dudu.App.Tests/Presentation/PresentationPolicyTests.cs`
- Create: `tests/Dudu.App.Tests/Notifications/NotificationPrivacyTests.cs`
- Modify: `src/Dudu.App/Hosting/AppHost.cs`

**Interfaces:**
- Consumes: `PetPresentation`, `IReminderDueSink`, `QuietHoursPolicy`, `PausePolicy`, `FullscreenDetector`, and Windows app notifications.
- Produces: `PresentationPolicy.Decide`, `PresentationCoordinator.PublishAsync`, `INotificationService.ShowReminderAsync`, and `ShowRemoteNoteArrivalAsync`.

- [ ] **Step 1: Write failing privacy and queue-release tests**

```csharp
[Fact]
public async Task Remote_notification_never_contains_ciphertext_or_plaintext()
{
    var sink = new RecordingNotificationSink();
    var service = new AppNotificationService(sink);

    await service.ShowRemoteNoteArrivalAsync(
        messageId: Guid.Parse("11111111-1111-4111-8111-111111111111"),
        TestContext.Current.CancellationToken);

    var notification = Assert.Single(sink.Items);
    Assert.Equal("A note arrived 💌", notification.Title);
    Assert.Null(notification.Body);
    Assert.Equal(
        "action=open-note&messageId=11111111-1111-4111-8111-111111111111",
        notification.ActivationArguments);
}

[Fact]
public void Leaving_quiet_hours_releases_one_durable_item_not_a_burst()
{
    var policy = PresentationPolicyFixture.WithQueuedNotes(3);

    var decision = policy.Decide(nowQuiet: false, fullscreen: false, paused: false);

    Assert.Single(decision.ToPresent);
    Assert.Equal(2, decision.RemainingQueuedCount);
}
```

- [ ] **Step 2: Run presentation tests to verify they fail**

Run: `dotnet test tests/Dudu.App.Tests/Dudu.App.Tests.csproj --filter "FullyQualifiedName~PresentationPolicyTests|FullyQualifiedName~NotificationPrivacyTests"`  
Expected: FAIL because notification and presentation services do not exist.

- [ ] **Step 3: Implement a single privacy and suppression gateway**

All unsolicited events pass through `PresentationCoordinator`. The policy:

- queues remote notes and non-bypass reminders during quiet hours, focus, fullscreen, session lock, or pause;
- discards ambient events instead of queueing them;
- releases at most one durable event, then enforces the configured minimum silent interval;
- allows explicit user actions immediately;
- falls back to the pet bubble when Windows notification registration fails.

`ReminderDueSink` implements `IReminderDueSink` in the application assembly and converts a committed reminder occurrence into the corresponding `PetEvent.ReminderDue` and notification request. This keeps `Dudu.Core` independent of WinUI and Windows notifications.

Register unpackaged app notifications at startup. Reminder notifications may include reminder title and `Done`/`Snooze` arguments. `ShowRemoteNoteArrivalAsync(Guid messageId, CancellationToken)` accepts no plaintext parameter and always emits only `A note arrived 💌`, no body, no image containing text, and a protocol-safe message ID in launch arguments.

- [ ] **Step 4: Run all offline desktop tests and harness notification check**

Run:

```powershell
dotnet test DuduDesktop.slnx -c Release
dotnet run --project tests/Dudu.WindowsHarness -- --scenario notifications
```

Expected: all tests pass. The harness shows a generic remote-note notification; disabling notifications causes the same event to appear through the pet bubble without data loss.

- [ ] **Step 5: Commit milestone 2**

```bash
git add src/Dudu.Core/Abstractions src/Dudu.App/Notifications src/Dudu.App/Presentation src/Dudu.App/Hosting/AppHost.cs tests
git commit -m "feat: enforce private calm presentation policy"
```

## Milestone 3 — Private sender

### Task 16: Define and prove the version-one cross-platform encryption protocol

**Files:**
- Create: `protocol/message-envelope.schema.json`
- Create: `protocol/message-payload.schema.json`
- Create: `protocol/README.md`
- Create: `src/Dudu.Infrastructure/Crypto/EncryptedEnvelope.cs`
- Create: `src/Dudu.Infrastructure/Crypto/EnvelopeCrypto.cs`
- Create: `src/Dudu.Infrastructure/Crypto/DesktopKeyService.cs`
- Create: `tests/Dudu.Infrastructure.Tests/Crypto/EnvelopeCryptoTests.cs`
- Create: `relay/package.json`
- Create: `relay/package-lock.json`
- Create: `relay/tsconfig.json`
- Create: `relay/tsconfig.sender.json`
- Create: `relay/src/protocol/types.ts`
- Create: `relay/sender-src/crypto.ts`
- Create: `relay/test/crypto.spec.ts`
- Create: `tools/Dudu.CryptoInterop/Dudu.CryptoInterop.csproj`
- Create: `tools/Dudu.CryptoInterop/Program.cs`

**Interfaces:**
- Consumes: `ISecretStore` and `ProductInfo.ProtocolVersion`.
- Produces: `DesktopKeyService.GetOrCreateAsync`, `EnvelopeCrypto.Decrypt`, TypeScript `encryptPayload`/`decryptPayloadForTest`, and protocol `EncryptedEnvelopeV1`.

- [ ] **Step 1: Write failing C# tamper tests and TypeScript round-trip tests**

```csharp
[Fact]
public void Decrypt_rejects_a_modified_authentication_tag()
{
    using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    var envelope = CryptoFixture.EncryptFor(recipient.ExportSubjectPublicKeyInfo(), "private hello");
    envelope = envelope with { Ciphertext = FlipLastBit(envelope.Ciphertext) };

    Assert.Throws<CryptographicException>(() =>
        EnvelopeCrypto.Decrypt(envelope, recipient.ExportPkcs8PrivateKey()));
}

[Fact]
public async Task Desktop_key_is_reused_from_Dpapi_secret_store()
{
    var store = new InMemorySecretStore();
    var service = new DesktopKeyService(store);

    var first = await service.GetOrCreateAsync(TestContext.Current.CancellationToken);
    var second = await service.GetOrCreateAsync(TestContext.Current.CancellationToken);

    Assert.Equal(first.PublicKeySpkiBase64Url, second.PublicKeySpkiBase64Url);
    Assert.Single(store.Values);
}
```

```typescript
it("uses fresh ephemeral keys and decrypts both envelopes", async () => {
  const recipient = await createRecipientForTest();
  const first = await encryptPayload(
    recipient.publicKey,
    payload("hello"),
    metadata("11111111-1111-4111-8111-111111111111"));
  const second = await encryptPayload(
    recipient.publicKey,
    payload("hello"),
    metadata("22222222-2222-4222-8222-222222222222"));

  expect(first.ephemeralPublicKey).not.toEqual(second.ephemeralPublicKey);
  await expect(decryptPayloadForTest(recipient.privateKey, first)).resolves.toEqual(payload("hello"));
  await expect(decryptPayloadForTest(recipient.privateKey, second)).resolves.toEqual(payload("hello"));
});
```

- [ ] **Step 2: Run both suites to verify they fail**

Run:

```powershell
dotnet test tests/Dudu.Infrastructure.Tests/Dudu.Infrastructure.Tests.csproj --filter FullyQualifiedName~EnvelopeCryptoTests
cd relay
npm test -- --run test/crypto.spec.ts
```

Expected: C# fails because crypto types are undefined; npm fails because `package.json` and protocol crypto are absent.

- [ ] **Step 3: Implement the exact wire contract in both runtimes**

Pin `relay/package.json` to Node `24.21.0`, TypeScript `7.0.2`, Vitest `5.0.0`, Wrangler `4.131.1`, `@cloudflare/vitest-plugin` `1.1.8`, and Playwright `1.63.0` with an exact lockfile. Configure `tsconfig.sender.json` to compile `sender-src` as ES2022 browser modules into `public/dist`; keep `public/dist` ignored because `npm run build` recreates it.

Define `EncryptedEnvelopeV1`:

```typescript
export interface EncryptedEnvelopeV1 {
  protocolVersion: 1;
  messageId: string;
  createdUtc: string;
  deliverAfterUtc: string | null;
  ephemeralPublicKey: string;
  hkdfSalt: string;
  nonce: string;
  ciphertext: string;
}

export interface RemoteMessagePayloadV1 {
  kind: "note";
  text: string;
  reaction: "none" | "wave" | "heart" | "hug" | "celebrate";
}
```

Use unpadded Base64URL for binary fields, UTF-8 JSON, P-256 ECDH, a 32-byte random HKDF salt, HKDF-SHA-256 info `DuduDesktop:message:v1`, AES-256-GCM, a 12-byte random nonce, and 128-bit authentication tags appended to Web Crypto ciphertext. Set AES-GCM additional authenticated data to the UTF-8 bytes of `1|messageId|createdUtc|deliverAfterUtc-or-empty`.

The desktop stores PKCS#8 private-key bytes under DPAPI key `desktop-ecdh-private-v1` and exports the public key as SPKI Base64URL. Reject a non-v1 envelope, invalid UUID message ID, future creation time beyond five minutes, invalid lengths, payload over 4,096 UTF-8 bytes, text over 2,000 Unicode scalar values, and unknown reaction.

- [ ] **Step 4: Run bidirectional interop rather than only same-runtime tests**

`Dudu.CryptoInterop` accepts `decrypt --private <pkcs8-file> --envelope <json-file>` and emits decrypted JSON to stdout. The Vitest interop test generates a recipient key and envelope, invokes this executable, and compares the parsed payload. A C# test invokes `node relay/dist/protocol/encrypt-fixture.js` with a public key and decrypts its stdout.

Run:

```powershell
dotnet build tools/Dudu.CryptoInterop/Dudu.CryptoInterop.csproj -c Release
dotnet test tests/Dudu.Infrastructure.Tests/Dudu.Infrastructure.Tests.csproj --filter FullyQualifiedName~Crypto
cd relay
npm ci
npm run build
npm test -- --run test/crypto.spec.ts
```

Expected: both same-runtime and bidirectional interop tests pass; changing AAD, nonce, ciphertext, or message ID makes decryption fail.

- [ ] **Step 5: Commit**

```bash
git add protocol relay/package.json relay/package-lock.json relay/tsconfig.json relay/tsconfig.sender.json relay/src/protocol relay/sender-src/crypto.ts relay/test/crypto.spec.ts src/Dudu.Infrastructure/Crypto tests/Dudu.Infrastructure.Tests/Crypto tools/Dudu.CryptoInterop DuduDesktop.slnx
git commit -m "feat: define interoperable encrypted note protocol"
```

### Task 17: Build the Worker foundation, D1 schema, device registration, and one-time pairing

**Files:**
- Create: `relay/wrangler.jsonc`
- Create: `relay/vitest.config.ts`
- Create: `relay/src/index.ts`
- Create: `relay/src/env.ts`
- Create: `relay/src/http/router.ts`
- Create: `relay/src/http/responses.ts`
- Create: `relay/src/security/tokens.ts`
- Create: `relay/src/security/cookies.ts`
- Create: `relay/src/security/origin.ts`
- Create: `relay/src/security/rateLimit.ts`
- Create: `relay/src/db/devices.ts`
- Create: `relay/src/db/pairings.ts`
- Create: `relay/src/routes/devices.ts`
- Create: `relay/src/routes/pairings.ts`
- Create: `relay/migrations/0001_identity.sql`
- Create: `relay/test/pairing.spec.ts`
- Create: `relay/test/tsconfig.json`

**Interfaces:**
- Consumes: protocol public-key encoding from Task 16.
- Produces: `POST /v1/devices/register`, `GET /v1/devices/current`, `POST /v1/devices/current/rotate-key`, `POST /v1/devices/pairing-code`, `POST /v1/pairings/redeem`, `GET /v1/sender/device`, `POST /v1/sender/disconnect`, and bearer/session authentication helpers.

- [ ] **Step 1: Write failing Worker-runtime pairing tests**

```typescript
it("redeems a pairing code once and sets a protected sender cookie", async () => {
  const registration = await registerDevice();
  const pairing = await createPairingCode(registration.desktopToken);

  const first = await exports.default.fetch("https://example.test/v1/pairings/redeem", {
    method: "POST",
    headers: jsonHeaders({ Origin: "https://example.test" }),
    body: JSON.stringify({ code: pairing.code }),
  });
  const second = await redeem(pairing.code);

  expect(first.status).toBe(200);
  expect(first.headers.get("set-cookie")).toMatch(/HttpOnly; Secure; SameSite=Strict/);
  expect((await first.json()).publicKey).toBe(TEST_PUBLIC_KEY);
  expect(second.status).toBe(410);
});

it("expires pairing codes after ten minutes", async () => {
  const { code } = await createPairingCodeForTest({ ageSeconds: 601 });
  expect((await redeem(code)).status).toBe(410);
});

it("stores hashes rather than raw capability tokens", async () => {
  const { desktopToken } = await registerDevice();
  const row = await env.DB.prepare("SELECT desktop_token_hash FROM devices").first();
  expect(row?.desktop_token_hash).not.toContain(desktopToken);
});
```

- [ ] **Step 2: Run Worker tests to verify they fail**

Run:

```powershell
cd relay
npm test -- --run test/pairing.spec.ts
```

Expected: FAIL because Worker configuration, migrations, and routes do not exist.

- [ ] **Step 3: Implement capability identity and atomic one-time pairing**

Configure `wrangler.jsonc` with compatibility date `2026-09-11`, a D1 binding named `DB`, static assets from `public`, no `nodejs_compat` flag, and a scheduled trigger `0 * * * *` for expiry cleanup.

Configure Vitest with `cloudflareTest()` from `@cloudflare/vitest-plugin`. Worker-runtime tests import `exports` and `env` from `cloudflare:workers` and call `exports.default.fetch(...)` so tests exercise the module Worker and real local D1 binding.

`0001_identity.sql` creates:

- `devices(id, public_key_spki, desktop_token_hash, created_utc, revoked_utc)`;
- `pairing_codes(code_hash, device_id, expires_utc, consumed_utc, attempt_count)`;
- `sender_sessions(id, device_id, token_hash, created_utc, expires_utc, revoked_utc)`;
- `rate_limit_buckets(key_hash, window_start_utc, count)`.

Generate 32 random bytes for capability/session tokens and store SHA-256 hashes only. Pairing codes contain eight Crockford characters excluding `I`, `L`, `O`, and `U`; store only a keyed HMAC-SHA-256 using Worker secret `PAIRING_CODE_PEPPER`. Registration is limited to five requests per IP hash per hour. Redemption is limited to ten attempts per IP hash and five attempts per code record.

Consume a code and create the sender session in one D1 batch guarded by `consumed_utc IS NULL AND expires_utc > current_time`. Set cookie `__Host-dudu_sender` with `Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=15552000`. Require exact same-origin `Origin` on cookie-authenticated mutations.

`GET /v1/devices/current` returns only device creation time, public-key fingerprint, and active sender-session count. Authenticated key rotation replaces the public key and desktop token, revokes all sender sessions, and deletes envelopes encrypted to the old key in one transaction; its response contains the new desktop token exactly once.

- [ ] **Step 4: Run migration and pairing tests**

Run:

```powershell
cd relay
npm run typecheck
npx wrangler d1 migrations apply dudu-relay-test --local
npm test -- --run test/pairing.spec.ts
```

Expected: typecheck passes; local migration applies once and is idempotently reported as already applied on a second run; pairing tests pass including one-time, expiry, rate-limit, cookie, disconnect, and revocation cases.

- [ ] **Step 5: Commit**

```bash
git add relay
git commit -m "feat: add private device registration and pairing"
```

### Task 18: Implement encrypted message queueing, scheduled delivery, acknowledgment, status, and expiry

**Files:**
- Create: `relay/migrations/0002_messages.sql`
- Create: `relay/src/db/messages.ts`
- Create: `relay/src/routes/messages.ts`
- Create: `relay/src/security/envelopeValidation.ts`
- Create: `relay/src/cleanup.ts`
- Modify: `relay/src/index.ts`
- Create: `relay/test/messages.spec.ts`
- Create: `relay/test/cleanup.spec.ts`

**Interfaces:**
- Consumes: desktop bearer auth, sender session auth, and `EncryptedEnvelopeV1`.
- Produces: `POST /v1/messages`, `GET /v1/messages`, `POST /v1/messages/{id}/ack`, `GET /v1/messages/{id}/status`, `DELETE /v1/devices/current`, and scheduled `cleanupExpired`.

- [ ] **Step 1: Write failing queue-lifecycle and no-read-receipt tests**

```typescript
it("queues idempotently and returns eligible ciphertext only to its desktop", async () => {
  const paired = await pairedFixture();
  const envelope = validEnvelope({ messageId: crypto.randomUUID() });

  const first = await paired.sender.postMessage(envelope);
  const duplicate = await paired.sender.postMessage(envelope);
  const messages = await paired.desktop.poll();

  expect(first.status).toBe(202);
  expect(duplicate.status).toBe(200);
  expect(messages).toEqual([envelope]);
});

it("ack deletes ciphertext and exposes only delivered-to-device status", async () => {
  const paired = await pairedFixtureWithMessage();

  await paired.desktop.ack(paired.messageId);

  expect(await messageCiphertext(paired.messageId)).toBeNull();
  expect(await paired.sender.status(paired.messageId)).toEqual({ status: "delivered" });
  expect(allRegisteredRoutes()).not.toContain("/v1/messages/:id/read");
});

it("does not return a scheduled envelope early", async () => {
  const paired = await pairedFixtureWithMessage({ deliverAfterUtc: addMinutes(now(), 5) });
  expect(await paired.desktop.poll()).toEqual([]);
});
```

- [ ] **Step 2: Run message tests to verify they fail**

Run: `cd relay; npm test -- --run test/messages.spec.ts test/cleanup.spec.ts`  
Expected: FAIL because message schema and routes do not exist.

- [ ] **Step 3: Implement bounded ciphertext lifecycle**

`0002_messages.sql` creates:

- `messages(id, device_id, protocol_version, created_utc, deliver_after_utc, expires_utc, ephemeral_public_key, hkdf_salt, nonce, ciphertext)` with unique `(device_id, id)`;
- `message_status(id, sender_session_id, state, updated_utc, expires_utc)`.

Validate exact JSON keys, protocol 1, UUID message IDs, ISO UTC timestamps, Base64URL form, 65–200 byte public-key encoding, 32-byte salt, 12-byte nonce, ciphertext from 17 through 6,144 bytes, delivery no more than 30 days ahead, and creation no more than five minutes in the future. Return `413` for size violations and `422` for semantic violations.

Sender insert uses `INSERT ... ON CONFLICT DO NOTHING` and returns the existing status for an identical message ID owned by the same session. Desktop poll returns at most 20 eligible messages ordered by delivery then creation. Acknowledgment transaction deletes `messages` and sets `message_status.state='delivered'`. Status rows expire after 24 hours. The hourly cleanup deletes expired messages/statuses, expired pairing codes, revoked sessions older than 24 hours, and empty rate-limit buckets.

`DELETE /v1/devices/current` requires desktop bearer auth, revokes the device, deletes queued messages and sender sessions transactionally, and returns `204`. No endpoint accepts or records `opened` or `read`.

- [ ] **Step 4: Run Worker, D1, and route-enumeration tests**

Run:

```powershell
cd relay
npm run typecheck
npm test -- --run test/messages.spec.ts test/cleanup.spec.ts
```

Expected: all tests pass, D1 contains no ciphertext after acknowledgment, expiry removes a 30-day-old undelivered message, and route enumeration confirms no read-receipt endpoint.

- [ ] **Step 5: Commit**

```bash
git add relay/migrations relay/src relay/test
git commit -m "feat: relay encrypted notes with bounded retention"
```

### Task 19: Build the paired mobile sender and browser-side encryption experience

**Files:**
- Create: `relay/public/index.html`
- Create: `relay/public/styles.css`
- Create: `relay/sender-src/main.ts`
- Create: `relay/sender-src/api.ts`
- Create: `relay/sender-src/pairing.ts`
- Create: `relay/sender-src/composer.ts`
- Create: `relay/sender-src/status.ts`
- Modify: `relay/sender-src/crypto.ts`
- Create: `relay/e2e/pairing.spec.ts`
- Create: `relay/e2e/sending.spec.ts`
- Create: `relay/playwright.config.ts`
- Modify: `relay/package.json`

**Interfaces:**
- Consumes: pairing/message HTTP endpoints and Task 16 protocol crypto.
- Produces: mobile pairing UI, `SenderApi`, `MessageComposer`, encrypted send flow, queued/delivered/expired/failed status, and disconnect.

- [ ] **Step 1: Write failing Playwright privacy and mobile-layout tests**

```typescript
test("pairs, previews locally, and sends ciphertext without leaking note text", async ({ page }) => {
  const api = await mockRelay(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await page.getByLabel("Pairing code").fill("7K9M2R4X");
  await page.getByRole("button", { name: "Pair privately" }).click();
  await page.getByLabel("Message").fill("I love you, good luck today");
  await page.getByRole("button", { name: "Preview" }).click();
  await expect(page.getByTestId("preview")).toContainText("I love you");
  await page.getByRole("button", { name: "Send note" }).click();

  expect(api.lastRequestBody).not.toContain("I love you");
  await expect(page.getByTestId("send-status")).toHaveText("Queued securely");
});

test("disconnect removes the paired composer", async ({ page }) => {
  await openPairedSender(page);
  await page.getByRole("button", { name: "Disconnect this phone" }).click();
  await expect(page.getByRole("button", { name: "Pair privately" })).toBeVisible();
});
```

- [ ] **Step 2: Run sender tests to verify they fail**

Run:

```powershell
cd relay
npx playwright install chromium
npm run test:e2e -- --project=chromium
```

Expected: FAIL because the sender assets and UI do not exist.

- [ ] **Step 3: Implement a framework-free accessible sender**

Use semantic HTML and TypeScript modules without React or a client router. `index.html` loads `/dist/main.js` generated by `tsc -p tsconfig.sender.json`. The unpaired state contains an eight-character code input and `Pair privately`. The paired state contains:

- message textarea with a 2,000-character counter;
- reaction radio group: none, wave, heart, hug, celebrate;
- optional `Send later` local date/time;
- local preview dialog;
- `Send note` button;
- recent in-browser statuses limited to 20 message IDs and no plaintext history;
- `Disconnect this phone`.

Before fetch, import the desktop SPKI public key, call `encryptPayload`, zero the mutable UTF-8 `Uint8Array` immediately after encryption, and send only `EncryptedEnvelopeV1`. JavaScript strings cannot be reliably zeroed, so do not claim protection against a compromised browser endpoint. Convert scheduled local date/time to an ISO UTC timestamp before encryption/upload. Disable send while uploading, retain unsent text after network failure, clear text only after `202`/idempotent `200`, and use an `AbortController` with a 15-second timeout.

Style for 320–768 px widths with a cream background, white/mica-like card, muted lavender focus ring, 44 px minimum targets, system fonts, `prefers-reduced-motion`, high contrast, and no remote fonts or analytics. Server headers set `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'`.

- [ ] **Step 4: Run browser crypto and sender journeys**

Run:

```powershell
cd relay
npm run typecheck
npm test -- --run test/crypto.spec.ts
npm run test:e2e
```

Expected: Chromium, Firefox, and WebKit projects pass at phone and desktop viewports; intercepted request bodies never contain entered note text; keyboard-only pairing and sending succeed; reduced-motion snapshot contains no transitions.

- [ ] **Step 5: Commit**

```bash
git add relay/public/index.html relay/public/styles.css relay/sender-src relay/tsconfig.sender.json relay/e2e relay/playwright.config.ts relay/package.json relay/package-lock.json
git commit -m "feat: add encrypted mobile note sender"
```

### Task 20: Integrate desktop pairing, polling, decryption, deduplication, and acknowledgment

**Files:**
- Create: `src/Dudu.Core/Abstractions/IRelayClient.cs`
- Create: `src/Dudu.Core/Abstractions/IRemoteNoteArrivalSink.cs`
- Create: `src/Dudu.Infrastructure/Remote/RelayContracts.cs`
- Create: `src/Dudu.Infrastructure/Remote/RelayClient.cs`
- Create: `src/Dudu.Infrastructure/Remote/RemoteSyncService.cs`
- Create: `src/Dudu.Infrastructure/Remote/PollBackoff.cs`
- Modify: `src/Dudu.Infrastructure/Data/Repositories/RemoteEnvelopeRepository.cs`
- Modify: `src/Dudu.Infrastructure/DependencyInjection.cs`
- Modify: `src/Dudu.App/ViewModels/ConnectionViewModel.cs`
- Modify: `src/Dudu.App/ViewModels/LoveNotesViewModel.cs`
- Create: `src/Dudu.App/Presentation/RemoteNoteArrivalSink.cs`
- Create: `tests/Dudu.Infrastructure.Tests/Remote/RelayClientTests.cs`
- Create: `tests/Dudu.Infrastructure.Tests/Remote/RemoteSyncServiceTests.cs`

**Interfaces:**
- Consumes: `DesktopKeyService`, `EnvelopeCrypto`, `ISecretStore`, relay HTTP API, SQLite remote-envelope transaction, and the core `IRemoteNoteArrivalSink` abstraction.
- Produces: `RemoteSyncService.StartAsync`, `StopAsync`, `CreatePairingCodeAsync`, `RevokeDeviceAsync`, `RevealAsync`, `PollBackoff.NextDelay`, and an `IPairingService` implementation.

- [ ] **Step 1: Write failing outage, deduplication, privacy, and acknowledgment tests**

```csharp
[Fact]
public async Task Duplicate_poll_is_stored_and_presented_once_then_acked()
{
    var fixture = RemoteSyncFixture.WithSameEnvelopeReturnedTwice();

    await fixture.Service.PollOnceAsync(fixture.CancellationToken);
    await fixture.Service.PollOnceAsync(fixture.CancellationToken);

    Assert.Single(fixture.Envelopes);
    Assert.Single(fixture.Presentations);
    Assert.Equal(new[] { fixture.MessageId }, fixture.AcknowledgedIds);
}

[Fact]
public async Task Ack_is_not_sent_when_local_transaction_fails()
{
    var fixture = RemoteSyncFixture.WithRepositoryFailure();

    await Assert.ThrowsAsync<RemoteSyncException>(
        () => fixture.Service.PollOnceAsync(fixture.CancellationToken));

    Assert.Empty(fixture.AcknowledgedIds);
}

[Fact]
public async Task Reveal_does_not_make_a_network_call_or_persist_plaintext()
{
    var fixture = RemoteSyncFixture.WithStoredEncryptedEnvelope("private hello");

    var note = await fixture.Service.RevealAsync(fixture.MessageId, fixture.CancellationToken);

    Assert.Equal("private hello", note.Text);
    Assert.Equal(0, fixture.Relay.RequestCount);
    Assert.DoesNotContain("private hello", fixture.ReadRawDatabaseText());
}
```

Add this deterministic backoff test:

```csharp
[Fact]
public void Backoff_is_capped_jittered_and_resets_after_success()
{
    var random = new SequenceRandomSource(0.5, 0.5, 0.5, 0.5, 0.5);
    var backoff = new PollBackoff(random);

    Assert.Equal(
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20),
         TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)],
        Enumerable.Range(0, 5).Select(_ => backoff.NextDelay()).ToArray());
    backoff.Reset();
    Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
}
```

- [ ] **Step 2: Run remote desktop tests to verify they fail**

Run: `dotnet test tests/Dudu.Infrastructure.Tests/Dudu.Infrastructure.Tests.csproj --filter "FullyQualifiedName~RelayClientTests|FullyQualifiedName~RemoteSyncServiceTests"`  
Expected: FAIL because relay client and sync service do not exist.

- [ ] **Step 3: Implement a nonblocking authenticated polling loop**

`RelayClient` uses one `HttpClient` with a 15-second timeout, JSON source-generation context, desktop bearer token from `ISecretStore`, bounded response bodies, and typed errors. Registration stores the desktop token only after the server response validates. Pairing creation returns code and expiry to `ConnectionViewModel`.

`RemoteSyncService` starts after all local services and never blocks AppHost startup. Poll online every 15 seconds; on network/5xx failure use jittered exponential backoff capped at 60 seconds; on 401 stop polling and expose `NeedsRepair`; on success reset backoff. Register it as `IPairingService` in place of `OfflinePairingService`.

For each envelope:

1. validate metadata and decrypt to a temporary buffer;
2. validate payload;
3. in one SQLite transaction insert encrypted envelope and processed message ID;
4. invoke `IRemoteNoteArrivalSink.NotifyAsync(Guid messageId, CancellationToken cancellationToken)`;
5. acknowledge device delivery;
6. zero temporary plaintext bytes.

On a duplicate processed ID, acknowledge without reinserting or presenting. `RevealAsync` decrypts from local ciphertext into memory, returns the payload, and does no network I/O. Dismiss deletes local ciphertext; save-to-jar explicitly writes only the selected plaintext. `RemoteNoteArrivalSink` lives in `Dudu.App` and translates the core sink call into `PresentationCoordinator.PublishAsync`, preserving the dependency direction `App -> Infrastructure -> Core`.

After recipient reveal, `LoveNotesViewModel` maps `wave`, `heart`, `hug`, and `celebrate` to their corresponding one-shot pet animations; `none` leaves Dudu in the normal opened-note pose. This local action occurs after reveal and never calls the relay.

- [ ] **Step 4: Run remote tests and a local Worker/desktop integration**

Run:

```powershell
cd relay
npx wrangler dev --local --port 8787
```

In a second terminal:

```powershell
$env:DUDU_RELAY_BASE_URL = "http://127.0.0.1:8787"
dotnet test tests/Dudu.Infrastructure.Tests/Dudu.Infrastructure.Tests.csproj --filter FullyQualifiedName~Remote
dotnet run --project tests/Dudu.WindowsHarness -- --scenario remote-note
```

Expected: remote tests pass. The harness registers, prints a pairing code, sends an encrypted fixture from the sender page, receives one generic notification within 30 seconds, reveals correct text locally, shows no plaintext in D1, and leaves no message ciphertext after acknowledgment.

- [ ] **Step 5: Commit milestone 3**

```bash
git add src/Dudu.Core/Abstractions src/Dudu.Infrastructure/Remote src/Dudu.Infrastructure/Data src/Dudu.Infrastructure/DependencyInjection.cs src/Dudu.App/ViewModels src/Dudu.App/Presentation/RemoteNoteArrivalSink.cs tests/Dudu.Infrastructure.Tests/Remote
git commit -m "feat: receive and reveal encrypted remote notes"
```

## Milestone 4 — Release candidate

### Task 21: Harden privacy, hostile-input handling, outage behavior, and end-to-end delivery

**Files:**
- Create: `src/Dudu.Infrastructure/Logging/PrivacySafeLog.cs`
- Create: `src/Dudu.Infrastructure/Remote/BoundedJsonContent.cs`
- Create: `tests/Dudu.Infrastructure.Tests/Security/PrivacyBoundaryTests.cs`
- Create: `relay/test/security.spec.ts`
- Create: `tests/e2e/private-note-flow.ps1`
- Create: `docs/privacy.md`
- Modify: `src/Dudu.Infrastructure/Remote/RelayClient.cs`
- Modify: `relay/src/index.ts`

**Interfaces:**
- Consumes: complete desktop and relay message path.
- Produces: `PrivacySafeLog` event methods, bounded JSON response reading, security headers, and `private-note-flow.ps1` release test.

- [ ] **Step 1: Write failing tests for plaintext leakage, oversized bodies, malformed envelopes, and outages**

```csharp
[Fact]
public async Task Logs_never_include_note_text_tokens_or_private_key_material()
{
    var fixture = PrivacyFixture.WithSecrets(
        note: "uniquely-private-phrase-7491",
        token: "desktop-token-8821",
        privateKeyMarker: "private-key-marker-3307");

    await fixture.ExerciseRegistrationPollFailureAndRevealAsync();

    var logs = fixture.LogSink.JoinedText;
    Assert.DoesNotContain("uniquely-private-phrase-7491", logs);
    Assert.DoesNotContain("desktop-token-8821", logs);
    Assert.DoesNotContain("private-key-marker-3307", logs);
}

[Fact]
public async Task Relay_client_rejects_response_larger_than_sixty_four_kib()
{
    var client = RelayClientFixture.RespondingWithBytes(65 * 1024);

    await Assert.ThrowsAsync<RelayProtocolException>(
        () => client.PollAsync(TestContext.Current.CancellationToken));
}

[Fact]
public async Task Worker_outage_does_not_stop_local_reminders()
{
    var fixture = AppFixture.WithUnavailableRelayAndDueReminder();

    await fixture.Host.StartAsync(fixture.CancellationToken);

    Assert.True(fixture.Host.IsRunning);
    Assert.Single(fixture.PresentedReminders);
}
```

```typescript
it.each([
  ["wrong origin", requestWithOrigin("https://evil.example"), 403],
  ["wrong content type", requestWithContentType("text/plain"), 415],
  ["unknown envelope key", requestWithEnvelope({ extra: true }), 422],
  ["oversized ciphertext", requestWithCiphertext(6145), 413],
])("rejects %s", async (_name, request, status) => {
  expect((await exports.default.fetch(request)).status).toBe(status);
});
```

- [ ] **Step 2: Run privacy and Worker security tests to verify they fail**

Run:

```powershell
dotnet test tests/Dudu.Infrastructure.Tests/Dudu.Infrastructure.Tests.csproj --filter FullyQualifiedName~PrivacyBoundaryTests
cd relay
npm test -- --run test/security.spec.ts
```

Expected: tests fail because bounded response and privacy-safe logging are not enforced.

- [ ] **Step 3: Centralize safe logging and defensive protocol boundaries**

Expose structured logging methods that accept IDs, counts, status codes, and exception types only:

```csharp
public static partial class PrivacySafeLog
{
    [LoggerMessage(1001, LogLevel.Information, "Relay poll returned {MessageCount} envelopes")]
    public static partial void PollCompleted(ILogger logger, int messageCount);

    [LoggerMessage(1002, LogLevel.Warning, "Relay request failed with {StatusCode} and category {Category}")]
    public static partial void RelayFailed(ILogger logger, int statusCode, string category);

    [LoggerMessage(1003, LogLevel.Warning, "Envelope {MessageId} was rejected as {Reason}")]
    public static partial void EnvelopeRejected(ILogger logger, Guid messageId, string reason);
}
```

Do not pass payload objects, headers, cookies, tokens, ciphertext, public/private keys, or response bodies to loggers. `BoundedJsonContent` streams at most 65,536 bytes and cancels beyond the boundary. Disable automatic HTTP body logging.

Worker responses include CSP, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Permissions-Policy: camera=(), microphone=(), geolocation=()`, and `Cache-Control: no-store` on API responses. Parse JSON through size-bounded reads and reject prototype keys, duplicate semantic fields, noncanonical Base64URL, and unknown properties.

Write `docs/privacy.md` with a table naming every local entity, whether it leaves the PC, its retention, deletion path, and backup inclusion. State the honest boundary: endpoint compromise can reveal an opened note; the relay cannot decrypt valid ciphertext.

- [ ] **Step 4: Run the full private-note flow and inspect all three storage surfaces**

Run:

```powershell
pwsh tests/e2e/private-note-flow.ps1 -RelayMode Local -NoteText "e2e-secret-4937"
```

The script starts local Wrangler, launches the Windows harness with a temporary data directory, registers and pairs Playwright, sends the note, waits up to 30 seconds, reveals it, acknowledges it, and searches desktop logs, D1 dumps, and captured HTTP traffic.

Expected: exit 0; recipient sees `e2e-secret-4937` only after reveal; the exact string is absent from logs, D1, and HTTP bodies; D1 ciphertext is deleted after acknowledgment; killing Wrangler does not stop a due local reminder.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.Infrastructure tests/Dudu.Infrastructure.Tests relay tests/e2e docs/privacy.md
git commit -m "test: harden private note and outage boundaries"
```

### Task 22: Produce the self-contained current-user installer, upgrade, and uninstall paths

**Files:**
- Create: `installer/DuduDesktop.iss`
- Create: `installer/assets/dudu.ico`
- Create: `installer/assets/wizard-small.bmp`
- Create: `scripts/publish-windows.ps1`
- Create: `tests/installer/installer-smoke.ps1`
- Modify: `src/Dudu.App/Dudu.App.csproj`
- Modify: `src/Dudu.App/System/StartupRegistrationService.cs`

**Interfaces:**
- Consumes: release publish output and current-user app paths.
- Produces: `artifacts/DuduDesktop-1.0.0-win-x64-private.exe`, installed `Dudu.App.exe`, upgrade-safe data retention, and uninstall data choice.

- [ ] **Step 1: Write the failing installer smoke test**

```powershell
param([Parameter(Mandatory)][string]$Installer)

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\DuduDesktop"
$dataRoot = Join-Path $env:LOCALAPPDATA "DuduDesktop"

Start-Process $Installer -ArgumentList "/VERYSILENT", "/CURRENTUSER", "/NORESTART" -Wait
if (-not (Test-Path (Join-Path $installRoot "Dudu.App.exe"))) {
    throw "Application executable was not installed for the current user."
}
if (-not (Test-Path (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Dudu Desktop.lnk"))) {
    throw "Start Menu shortcut is missing."
}

& (Join-Path $installRoot "Dudu.App.exe") --self-test
if ($LASTEXITCODE -ne 0) { throw "Installed application self-test failed." }
```

- [ ] **Step 2: Run the smoke test to verify it fails**

Run: `pwsh tests/installer/installer-smoke.ps1 -Installer artifacts/DuduDesktop-1.0.0-win-x64-private.exe`  
Expected: FAIL because no installer artifact exists.

- [ ] **Step 3: Implement reproducible publish and Inno Setup 7.1.0 packaging**

`publish-windows.ps1` removes only `artifacts/publish/win-x64` after resolving and verifying that exact path is beneath the repository `artifacts` directory, then runs:

```powershell
dotnet publish src/Dudu.App/Dudu.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:WindowsAppSDKSelfContained=true `
  -p:PublishSingleFile=false `
  -p:DebugType=embedded `
  -o artifacts/publish/win-x64
& "${env:ProgramFiles}\Inno Setup 7\ISCC.exe" installer/DuduDesktop.iss
```

Set these installer directives:

```ini
[Setup]
AppId={{FC8FF35F-5A84-41EA-9E84-23A8EF06F1F6}
AppName=Dudu Desktop
AppVersion=1.0.0
DefaultDirName={localappdata}\Programs\DuduDesktop
DefaultGroupName=Dudu Desktop
PrivilegesRequired=lowest
SetupArchitecture=x64
OutputDir=..\artifacts
OutputBaseFilename=DuduDesktop-1.0.0-win-x64-private
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Dudu.App.exe
CloseApplications=yes
RestartApplications=no
```

Package the complete publish directory and private asset packs. Create current-user Start menu and optional desktop shortcuts. Do not create the sign-in shortcut in the installer; preserve the recipient's in-app preference during upgrades. The uninstall Pascal page offers `Keep my notes and settings` checked by default. When unchecked, it closes the app and deletes only the exact `{localappdata}\DuduDesktop` directory after validating the path suffix. Remote-device deletion remains an explicit in-app network action.

Add `--self-test` to open the database, validate bundled manifests, resolve services, and exit without showing UI or changing user settings.

- [ ] **Step 4: Test clean install, in-place upgrade, and both uninstall choices**

Run:

```powershell
pwsh scripts/publish-windows.ps1 -Version 1.0.0
pwsh tests/installer/installer-smoke.ps1 -Installer artifacts/DuduDesktop-1.0.0-win-x64-private.exe
```

The smoke script then writes a marker local note, installs the same package again, verifies the marker survives, uninstalls once with data preserved, reinstalls and verifies the marker, then uninstalls with `/DELETEUSERDATA=1` and verifies only `%LOCALAPPDATA%\DuduDesktop` is removed.

Expected: every phase exits 0; install root is per-user; no UAC prompt appears; the app launches on clean Windows 11 24H2; program files are removed on uninstall; the requested data-retention choice is honored.

- [ ] **Step 5: Commit**

```bash
git add installer scripts/publish-windows.ps1 tests/installer src/Dudu.App
git commit -m "build: package private current-user Windows installer"
```

### Task 23: Meet accessibility, UI automation, stability, and performance release gates

**Files:**
- Create: `tests/Dudu.UiTests/AccessibilityTests.cs`
- Create: `tests/Dudu.UiTests/FullJourneyTests.cs`
- Create: `tests/Dudu.WindowsHarness/PerformanceScenario.cs`
- Create: `tests/Dudu.WindowsHarness/LongRunScenario.cs`
- Create: `scripts/run-performance-gates.ps1`
- Create: `docs/testing/windows-acceptance.md`
- Modify: `src/Dudu.App/Pages/*.xaml`
- Modify: `src/Dudu.App/Themes/*.xaml`
- Modify: `src/Dudu.App/Animation/AnimationEngine.cs`

**Interfaces:**
- Consumes: installed release candidate and all stable automation IDs.
- Produces: keyboard/accessibility evidence, CPU/memory/startup metrics JSON, and an eight-hour stability report.

- [ ] **Step 1: Write failing UI accessibility and performance assertions**

```csharp
[Fact]
public void Every_actionable_settings_control_has_name_and_keyboard_focus()
{
    using var app = DuduUiFixture.LaunchFresh();
    foreach (var control in app.AllActionableControls())
    {
        Assert.False(string.IsNullOrWhiteSpace(control.Name), control.AutomationId);
        Assert.True(control.IsKeyboardFocusable, control.AutomationId);
    }
}

[Fact]
public void Text_scaling_at_two_hundred_percent_has_no_clipped_primary_actions()
{
    using var app = DuduUiFixture.LaunchFresh(textScalePercent: 200);
    app.VisitEverySettingsPage();

    Assert.Empty(app.FindClippedControls(minimumVisiblePercent: 95));
}
```

`PerformanceScenario` records process launch-to-first-overlay, average CPU after five idle minutes, average CPU during three minutes of animation, peak working set, and managed allocation trend. It exits nonzero when startup exceeds 3 seconds, idle CPU reaches 1%, animation CPU reaches 3%, working set reaches 200 MB, or allocation slope exceeds 1 MB/hour.

- [ ] **Step 2: Run tests and gates to establish failures**

Run:

```powershell
dotnet test tests/Dudu.UiTests/Dudu.UiTests.csproj --filter "FullyQualifiedName~AccessibilityTests|FullyQualifiedName~FullJourneyTests"
pwsh scripts/run-performance-gates.ps1 -Executable artifacts/publish/win-x64/Dudu.App.exe
```

Expected before tuning: at least one missing automation name or clipped control is reported, or a measured performance gate fails. Preserve the generated baseline JSON under `artifacts/performance/baseline.json` without committing it.

- [ ] **Step 3: Fix measured accessibility and performance failures at their source**

For every reported control, add a concise accessible name, keyboard order, visible focus treatment, and tooltip only when the label is insufficient. Ensure all page bodies use `ScrollViewer` and responsive `Grid`/`ItemsRepeater` layouts rather than fixed heights. Ensure statuses use icon plus text, not color alone. At high contrast, use system brushes. In reduced motion, disable translation and frame loops and retain only immediate pose changes or opacity fades under 100 ms.

For performance, keep one decoded frame cache per active asset pack, one reusable render buffer per size, one scheduler timer, prepared SQLite statements for hot queries, and one relay `HttpClient`. Suspend ambient animation ticks while hidden, fullscreen-suppressed, session-locked, or display-off. Dispose bitmaps when changing packs and verify `GC.GetTotalAllocatedBytes` slope after warmup.

`FullJourneyTests` automates onboarding, creates and completes a reminder and task, runs a one-minute accelerated focus session, records a check-in, reveals a fixture remote note, changes theme/outfit/reduced-motion settings, backs up data, restarts, and verifies persistence.

- [ ] **Step 4: Pass automated gates and perform the eight-hour run**

Run:

```powershell
dotnet test DuduDesktop.slnx -c Release
dotnet test tests/Dudu.UiTests/Dudu.UiTests.csproj
pwsh scripts/run-performance-gates.ps1 -Executable artifacts/publish/win-x64/Dudu.App.exe -Output artifacts/performance/release.json
dotnet run --project tests/Dudu.WindowsHarness -c Release -- --scenario long-run --hours 8 --output artifacts/stability/eight-hour.json
```

Expected: all tests pass; release JSON is within every specified threshold; eight-hour report shows no crash, no duplicate reminder/note, no HWND/GDI handle growth above 5%, and no memory growth slope above 1 MB/hour.

- [ ] **Step 5: Commit**

```bash
git add src/Dudu.App tests/Dudu.UiTests tests/Dudu.WindowsHarness scripts/run-performance-gates.ps1 docs/testing/windows-acceptance.md
git commit -m "test: meet Windows accessibility and performance gates"
```

### Task 24: Create one-command verification and build the private version-one release

**Files:**
- Create: `scripts/verify.ps1`
- Create: `docs/release.md`
- Modify: `README.md`
- Modify: `docs/testing/windows-acceptance.md`
- Create: `CHANGELOG.md`

**Interfaces:**
- Consumes: every build, test, relay, UI, performance, and installer command.
- Produces: a deterministic verification exit code, release checklist, SHA-256 manifest, and final private installer.

- [ ] **Step 1: Write the verification script as an executable acceptance contract**

`verify.ps1` must stop on first error and run:

```powershell
$ErrorActionPreference = "Stop"
dotnet --version
node --version
dotnet restore DuduDesktop.slnx --locked-mode
dotnet build DuduDesktop.slnx -c Release --no-restore
dotnet test DuduDesktop.slnx -c Release --no-build --collect:"XPlat Code Coverage"

Push-Location relay
npm ci
npm run typecheck
npm test -- --run
npm run test:e2e
Pop-Location

pwsh scripts/publish-windows.ps1 -Version 1.0.0
pwsh tests/installer/installer-smoke.ps1 `
  -Installer artifacts/DuduDesktop-1.0.0-win-x64-private.exe
pwsh scripts/run-performance-gates.ps1 `
  -Executable artifacts/publish/win-x64/Dudu.App.exe `
  -Output artifacts/performance/release.json
pwsh tests/e2e/private-note-flow.ps1 -RelayMode Local -NoteText "verification-secret-1042"
```

After those commands, compute SHA-256 for the installer and write `artifacts/SHA256SUMS.txt`. Reject any tracked file under `assets/raw`, any source manifest missing `privateUseOnly: true`, any relay dependency not represented in `package-lock.json`, and any git working-tree change.

- [ ] **Step 2: Run the verification script once and record every failure**

Run: `pwsh scripts/verify.ps1`  
Expected on the first full pass: any stale file path, nondeterministic test, warning, missing acceptance evidence, or dirty tree makes the script exit nonzero with the failing command.

- [ ] **Step 3: Resolve each concrete verification failure and document private operations**

`docs/release.md` includes:

- Windows/Visual Studio/Node/Inno prerequisites;
- local D1 setup and migration commands;
- Cloudflare secret creation for `PAIRING_CODE_PEPPER`;
- production D1 creation and binding ID insertion;
- `npm exec wrangler deploy` and rollback to the prior Worker version;
- desktop build, checksum, installation, upgrade, and uninstall;
- first pairing and sender revocation;
- database backup/restore and damaged-database preservation;
- statement that SmartScreen may warn because the private installer is unsigned;
- statement that the artwork and installer must remain private.

`CHANGELOG.md` records version `1.0.0-private.1` and every user-visible v1 feature. `README.md` links the design, implementation plan, privacy document, testing matrix, and release runbook.

- [ ] **Step 4: Execute final release verification and complete the manual matrix**

Run:

```powershell
pwsh scripts/verify.ps1
```

Then complete `docs/testing/windows-acceptance.md` on a clean Windows 11 24H2 x64 PC for 100%, 125%, 150%, and 200% DPI; single/multiple monitor and removal; sleep/resume; lock/unlock; Explorer restart; fullscreen game/video; notification enabled/unavailable; offline launch; sign-in launch; backup/restore; install/upgrade/uninstall; and eight-hour stability.

Expected: automated verification exits 0; every manual row has pass status, Windows build, timestamp, tester, and evidence path; `artifacts/DuduDesktop-1.0.0-win-x64-private.exe` and `artifacts/SHA256SUMS.txt` exist.

- [ ] **Step 5: Commit and tag the private release**

```bash
git add README.md CHANGELOG.md docs scripts/verify.ps1
git commit -m "release: prepare Dudu Desktop private v1"
git tag -a v1.0.0-private.1 -m "Dudu Desktop private v1"
```

Do not push the tag or repository to a public remote. Transfer only the installer and its SHA-256 checksum privately to the recipient.

## Final acceptance checklist

- [ ] Onboarding completes in under two minutes on a clean Windows 11 24H2 x64 account.
- [ ] The overlay has transparent edges, no taskbar/Alt+Tab entry, alpha-aware input, DPI-safe placement, and no focus theft.
- [ ] Reminders, tasks, focus, countdowns, comfort, outfits, manual check-ins, and local notes work with the network disabled.
- [ ] Quiet hours, all pause modes, reduced motion, and fullscreen suppression gate unsolicited behavior.
- [ ] A mobile browser pairs once with a ten-minute one-time code and retains a secure HttpOnly session.
- [ ] Browser-to-desktop encrypted notes interoperate across P-256 ECDH, HKDF-SHA-256, and AES-256-GCM.
- [ ] An online desktop receives a normal remote note within 30 seconds.
- [ ] The relay, logs, notifications, and packet capture never contain remote-note plaintext.
- [ ] Duplicate delivery produces exactly one visible note.
- [ ] Opening a note performs no network request and creates no read receipt.
- [ ] Acknowledgment removes D1 ciphertext; undelivered messages expire at 30 days.
- [ ] Database migrations back up first and preserve failed databases for recovery.
- [ ] Missing artwork falls back to the original neutral pose.
- [ ] CPU, memory, startup, allocation, accessibility, and eight-hour stability gates pass.
- [ ] Current-user install, upgrade, uninstall, optional data deletion, and launch-at-sign-in pass without elevation.
- [ ] The repository, artwork, Worker URL, sender page, installer, and release tag remain private.

## Implementation references

- Approved design: `docs/superpowers/specs/2026-09-11-dudu-desktop-companion-design.md`
- .NET support policy: <https://dotnet.microsoft.com/en-us/platform/support/policy>
- Windows App SDK structure: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/windows-app-sdk-app-structure>
- Win32 layered windows: <https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features>
- Windows app notifications: <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/>
- Cloudflare D1 Worker API: <https://developers.cloudflare.com/d1/worker-api/>
- Cloudflare Workers testing: <https://developers.cloudflare.com/workers/testing/>
- Inno Setup 7: <https://jrsoftware.org/isdl.php>

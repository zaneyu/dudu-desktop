# Cute-Focus Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Strip Dudu Desktop down to a cute companion — pet, affection interactions, partner love notes, UK clock, minimal settings — by hard-deleting every purely practical feature.

**Architecture:** Mostly deletion, done feature-slice by feature-slice so the solution compiles and the runnable test suites stay green after every task. UI consumers are removed first (pages, viewmodels, navigation), then each feature's services/state are removed end-to-end (App → Infrastructure → Core), preserving the `Dudu.App → Dudu.Infrastructure → Dudu.Core` direction. No database migrations; dormant tables/columns stay.

**Tech Stack:** C# / .NET 10.0.112 (pinned by `global.json`), WinUI 3 / Windows App SDK, SQLite (Microsoft.Data.Sqlite), xUnit v3 (Microsoft Testing Platform), SkiaSharp. Relay (TypeScript/Cloudflare) is untouched.

**Spec:** `docs/superpowers/specs/2026-09-27-cute-focus-cleanup-design.md` — read it in full before Task 1. When this plan and the spec disagree, the spec wins; note the disagreement in the task's commit message.

## Global Constraints

- Branch: `feat/cute-focus`. Never commit to or push `main`. Pushing `feat/cute-focus` is allowed (needed for Windows CI dispatch). Merging to `main` needs owner approval.
- Dependency direction `Dudu.App → Dudu.Infrastructure → Dudu.Core`; no UI types in Core.
- Never commit `bin/`, `obj/`, `artifacts/`, `work/`, `outputs/`, `node_modules/`, `.wrangler/`, `relay/dist/`, or any `packages.lock.json`. Move generated lock files to `work/generated-package-locks/`.
- No new migrations. No table/column drops. Migration 0001 NOT NULL no-default columns (`quiet_hours_enabled`, `quiet_hours_start`, `quiet_hours_end`, `local_note_daily_limit`) keep being written on INSERT with fixed values `0`, `'22:00'`, `'07:00'`, `0`.
- `PresentationItemKind.RemoteNote` keeps its exact name (persisted in `held_presentations.kind`).
- `PauseMode` enum members and `PausePersistence.ToPreference/FromPreference` behavior unchanged.
- Asset manifest contract (`AssetManifest` required keys incl. `focus`) unchanged.
- Keep `DatabaseBackupService` and all automatic backup/restore behavior; remove only user-facing backup/restore commands.
- Never log note text, tokens, pairing codes, keys, ciphertext, URLs, or bodies. Relay path logs only via `PrivacySafeLog`.
- UI copy keeps the existing lower-case cute tone (e.g. "okkk", "otayyy").
- Delete tests of removed features; never weaken assertions of kept features to make them pass.
- A zero-test run is not a pass.
- Relay URL, sender page, artwork, packages stay private — never paste them into commits, logs, or chat.

## Environment (cloud / Linux or macOS)

The Mac instructions in `AGENTS.md` use a vendored SDK under `work/` which is **not** in git. In a fresh cloud checkout:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version 10.0.112 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"; export DUDU_DOTNET="$DOTNET_ROOT/dotnet"
dotnet --version   # must print 10.0.112
```

On the Mac, use the `AGENTS.md` vendored-toolchain exports instead.

Define these once per shell (used by every task's verification step):

```bash
export DUDU_REPO="$(git rev-parse --show-toplevel)"
DUDU_MAC_STUB_FLAGS=(-c Release --no-restore -p:RuntimeIdentifier=win-x64 -p:PlatformTarget=x64
  -p:WindowsAppSDKSelfContained=false -p:WindowsPackageType=None -p:AppxGeneratePriEnabled=false
  -p:GenerateAppInstallerFile=false -p:AppxPackageSigningEnabled=false -p:EnableCoreMrtTooling=false
  -p:ExpandPriResources=false -p:EnableDefaultApplicationDefinition=false -p:EnableDefaultPageItems=false)
```

**Gate G (run at the end of every task, in this order — AGENTS.md: Core/Infra tests before any stub build):**

```bash
cd "$DUDU_REPO"
dotnet restore DuduDesktop.slnx
dotnet build tests/Dudu.Infrastructure.Tests/Dudu.Infrastructure.Tests.csproj -c Release --no-incremental
dotnet build tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj -c Release --no-incremental
tests/Dudu.Infrastructure.Tests/bin/Release/net10.0-windows10.0.26100.0/Dudu.Infrastructure.Tests -noLogo -noColor -longRunning 60
tests/Dudu.Core.Tests/bin/Release/net10.0/Dudu.Core.Tests -noLogo -noColor -longRunning 60
dotnet restore tests/Dudu.App.Tests/Dudu.App.Tests.csproj -r win-x64
find . -name packages.lock.json -not -path './work/*' -exec sh -c 'mkdir -p work/generated-package-locks && mv "$1" "work/generated-package-locks/$(echo "$1" | tr / _)"' _ {} \;
for p in src/Dudu.App/Dudu.App.csproj tests/Dudu.App.Tests/Dudu.App.Tests.csproj tests/Dudu.UiTests/Dudu.UiTests.csproj tests/Dudu.WindowsHarness/Dudu.WindowsHarness.csproj; do
  dotnet build "$p" "${DUDU_MAC_STUB_FLAGS[@]}" || exit 1
done
git status --short   # only intended source changes
```

Expected: Infra and Core runs report `Failed: 0` with a non-zero total (Infra has a few expected skips: DPAPI + optional live relay). All four stub builds succeed with 0 errors. `Dudu.App.Tests` cannot execute off Windows — it must compile; its behavior is verified in Task 15 on Windows CI.

**Test sketches:** code blocks in this plan show intent and the real APIs where they were verified. Before writing each test, open the named existing test file and reuse its real fakes/helpers; never invent a helper name. Verified helpers: Infra DB tests use a private `DatabaseFixture.CreateAsync()` pattern (`Database.OpenAsync(new DatabaseOptions(dbPath, backupsPath), ct)`, see `tests/Dudu.Infrastructure.Tests/Data/DatabaseTests.cs:1991-2027`) — copy that pattern into new test files; `AppHost` tests live in **`tests/Dudu.Infrastructure.Tests/Hosting/AppHostTests.cs`** (runs on Linux via the linked `AppHost.cs`) with `AppHostFixture`, `TestTimerFactory.Timer.Signal()`, `TestPresentationGateway`, `TestVisibilityReconciler`; the held-queue fake is `RecordingHeldPresentationRepository.Seed(HeldPresentation)` / `.Rows` (a dictionary) in `PresentationHeldQueuePersistenceTests.cs`; pet events go through `PetStateMachine.Handle(...)`, e.g. `new PetEvent.EatingStarted("meal-1")`.

**Gate G does not build `tools/`;** CI does. In any task that deletes a public type, also `grep -rn <TypeName> tools` and fix hits.

**Important:** App-level tests (Dudu.App.Tests) only compile here. For every App-level behavior test the plan adds, write it, make it compile, and rely on Windows CI (Task 15) to execute it. Do not skip writing them.

## Review Focus

1. **Existing install upgrading with legacy data** — `held_presentations` rows of kind `Reminder`/`LocalNote`, enabled default reminders, a persisted `pause_mode` of any legacy value, seeded `local_notes` rows: the app must start cleanly, never surface those, and not spam diagnostics. (Tests in Tasks 6, 8, 12.)
2. **Old toast clicked after upgrade** — a reminder toast still in Action Center clicked while the app is cold or running must parse to `null` (no action, no navigation to a removed page, no crash). (Task 6.)
3. **Partner note arriving while eating/dragging/paused/fullscreen** — must still be held and delivered later, never lost, and forget-pairing must still purge held copies. (Tasks 7, 8.)
4. **Delete my data** — sync stopped before the remote call; remote failure wipes nothing, restarts sync and offers "wipe this pc only"; offline build / safe mode wipe local only without an error. (Task 3.)
6. **Click-to-pet survives the overlay action-surface removal.** (Task 11.)
5. **Self-test / installer smoke** — `--self-test` must pass with the reduced service graph. (Task 12.)

---

### Task 1: Baseline

**Files:** none modified.

- [ ] **Step 1:** Set up the environment above; run Gate G on the unmodified branch. Record the exact pass/skip/total counts for Core and Infra and any pre-existing stub-build warnings in a scratch note (not committed). If the baseline itself fails, stop and report — do not start deleting on a red baseline.
- [ ] **Step 2:** Read the spec, `AGENTS.md` (Diagnostics section), and skim `src/Dudu.App/Hosting/WindowsCompanionProductionComposition.cs` (the composition root; almost every task edits it).

---

### Task 2: Navigation shell → 3 destinations; delete Reminders/Tasks/Privacy/Connection pages

Removes the UI for the cut features first so later tasks can delete services without chasing page code.

**Files:**
- Modify: `src/Dudu.App/ViewModels/SettingsShellViewModel.cs`
- Modify: `src/Dudu.App/Windows/SettingsWindow.xaml`, `src/Dudu.App/Windows/SettingsWindow.xaml.cs` (nav items, page map)
- Modify: `src/Dudu.App/XamlCompileStubs.cs` (stub page types and tags)
- Rename: `src/Dudu.App/Pages/AppearancePage.xaml(.cs)` → `SettingsPage.xaml(.cs)`; `src/Dudu.App/ViewModels/AppearanceViewModel.cs` → `SettingsViewModel.cs` (class rename; keep members for now)
- Delete: `src/Dudu.App/Pages/RemindersPage.xaml(.cs)`, `TasksFocusPage.xaml(.cs)`, `PrivacyDataPage.xaml(.cs)`, `ConnectionPage.xaml(.cs)`; `src/Dudu.App/ViewModels/RemindersViewModel.cs`, `TasksFocusViewModel.cs`, `PrivacyDataViewModel.cs`
- Modify: `src/Dudu.App/Pages/SettingsPage.xaml(.cs)` — host the `ConnectionViewModel` section ("Partner connection") moved from `ConnectionPage.xaml` **and its code-behind**: the 15 s `_expiryTimer`, `Page_Loaded` → `RefreshAsync`, `Page_Unloaded` (stop timer), and `PropertyChanged` → `RefreshStatusText` from `ConnectionPage.xaml.cs`; move the Connection stub fields in `XamlCompileStubs.cs` (~lines 180-194) onto the `SettingsPage` stub; change `SettingsWindow.xaml.cs` `RefreshCurrentPage` (~lines 175-183) so the Settings page refreshes its connection section (no separate Connection branch)
- Create: `src/Dudu.App/ViewModels/FocusDisplay.cs` — move `public static class FocusDisplay` out of `TasksFocusViewModel.cs` (~line 457) verbatim, because `HomeViewModel` still uses it until Task 4 (delete it in Task 7 once unreferenced)
- Modify: `src/Dudu.App/Overlay/ActionBubbleLayout.cs`, `OverlayCommandRouter.cs`, `src/Dudu.App/Pages/HomePage.xaml` — remove `OverlayAction.StartFocus` and `OverlayAction.Tasks` now (enum members, their `Label`/`AutomationId` arms, router `ExecuteAsync`/`ExecuteAccessibleAsync`/`EquivalentSettingsDestination` arms, `ExecuteStartFocusAsync` which constructs `TasksFocusViewModel`, and the two Home buttons tagged `StartFocus`/`Tasks`), since `TasksFocusViewModel` is deleted here
- Modify: `src/Dudu.App/Overlay/OverlayCommandRouter.cs`, `src/Dudu.App/Overlay/ActionBubbleLayout.cs`, `src/Dudu.App/Notifications/AppNotificationService.cs` — retarget any `"reminders"`, `"tasks"`, `"privacy"`, `"connection"`, `"appearance"` tag (DrinkWater's `EquivalentSettingsDestination` → `"home"`, like Pet and EatTogether)
- Modify: `src/Dudu.App/Pages/OnboardingPage.xaml` if it references a removed tag
- Test: `tests/Dudu.App.Tests/ViewModels/SettingsShellViewModelTests.cs` (create if absent), and fix/delete: `tests/Dudu.App.Tests/ViewModels/RemindersTasksLoveNotesUxTests.cs`, `ReminderPageActionTests.cs`, `ReminderScheduleSummaryTests.cs`, `PrivacyDataPageUxTests.cs`, `ConnectionPageUxTests.cs` (move connection assertions that still apply into a `SettingsPageConnectionTests.cs`), `FeatureViewModelTests.cs` (remove sections for deleted VMs), `OverlayCommandRouterTests.cs`, `ActionBubbleLayoutTests.cs` (~line 105), `FeatureViewModelTests.cs` (~1204-1205, 2481, 2499) (StartFocus/Tasks cases only), `tests/Dudu.App.Tests/Ui/XamlContractTests.cs`, `tests/Dudu.App.Tests/Hosting/CompanionCompositionTests.cs`, `tests/Dudu.App.Tests/System/AwaitableUiDispatcherTests.cs`, `tests/Dudu.App.Tests/Overlay/LayeredFramePresenterTests.cs` (tag strings only)

**Interfaces:**
- Produces: destination tags `home`, `notes`, `settings`; automation IDs `NavHome`, `NavLoveNotes`, `NavSettings`; `SettingsViewModel` (ex-`AppearanceViewModel`) exposing a `Connection` property of type `ConnectionViewModel`; `SettingsPage`.

- [ ] **Step 1: Write the failing test**

```csharp
using Dudu.App.ViewModels;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

public sealed class SettingsShellViewModelTests
{
    [Fact]
    public void Shell_exposes_exactly_home_notes_and_settings()
    {
        var shell = new SettingsShellViewModel();

        Assert.Equal(
            ["home", "notes", "settings"],
            shell.Destinations.Select(d => d.Tag).ToArray());
        Assert.Equal(
            ["NavHome", "NavLoveNotes", "NavSettings"],
            shell.Destinations.Select(d => d.AutomationId).ToArray());
    }

    [Theory]
    [InlineData("reminders")]
    [InlineData("tasks")]
    [InlineData("appearance")]
    [InlineData("connection")]
    [InlineData("privacy")]
    public void Removed_destinations_are_rejected(string tag)
    {
        Assert.False(new SettingsShellViewModel().Navigate(tag));
    }
}
```

- [ ] **Step 2:** Change `DestinationValues` to:

```csharp
private static readonly SettingsDestination[] DestinationValues =
[
    new("home", "Home", "NavHome"),
    new("notes", "Love Notes", "NavLoveNotes"),
    new("settings", "Settings", "NavSettings"),
];
```

- [ ] **Step 3:** Delete the pages/VMs listed; rename Appearance → Settings (`git mv`, then rename class/`x:Class`/usages); move the Connection XAML block into `SettingsPage.xaml` under a "Partner connection" header, binding to `ViewModel.Connection.*`; update `SettingsWindow` nav + page map and `XamlCompileStubs`; retarget every removed tag found by:

```bash
grep -rn '"reminders"\|"tasks"\|"privacy"\|"connection"\|"appearance"' src tests --include='*.cs' --include='*.xaml' | grep -v /obj/
```

Expected after edits: no hits except test data that asserts rejection. Composition wiring for the deleted VMs in `WindowsCompanionProductionComposition.cs` is removed; services they used stay registered for now (later tasks delete them).
- [ ] **Step 4:** Delete/fix the test files listed. Run Gate G. Expected: green.
- [ ] **Step 5: Commit** — `refactor(ui): collapse settings to home, love notes, settings`

---

### Task 3: Settings page contents — startup, delete my data

**Files:**
- Modify: `src/Dudu.App/ViewModels/SettingsViewModel.cs`, `src/Dudu.App/Pages/SettingsPage.xaml(.cs)`
- Modify: `src/Dudu.App/Pages/HomePage.xaml(.cs)` — remove launch-at-sign-in checkbox + retry panel (move to Settings with the `StartupSettingsService` wiring from `HomePage.xaml.cs`)
- Modify: `src/Dudu.App/ViewModels/CompanionFeatureContext.cs` — add optional ctor params / properties `Func<CancellationToken, Task>? stopRemoteSyncAsync`, `Func<CancellationToken, Task>? startRemoteSyncAsync` (default no-op) and `bool remoteDeleteAvailable` (default `false`) → `RemoteDeleteAvailable`
- Modify: `src/Dudu.App/Hosting/WindowsCompanionProductionComposition.cs` — production context: `var remoteSync = services.GetService<RemoteSyncService>();` `remoteDeleteAvailable: remoteSync is not null`, `stopRemoteSyncAsync: t => remoteSync?.StopAsync(t) ?? Task.CompletedTask`, `startRemoteSyncAsync: t => remoteSync?.StartAsync(t) ?? Task.CompletedTask`. The existing `deleteRemoteDataAsync` delegate (throws `NotSupportedException` when `!result.Completed`, ~line 1062-1070) and `deleteLocalDataAsync` (already stops sync — idempotent) stay. Safe-mode context: leave `remoteDeleteAvailable` false.
- Test: `tests/Dudu.App.Tests/ViewModels/DeleteMyDataTests.cs` (create), containing a private `DeleteMyDataHarness` that builds a real `CompanionFeatureContext` with recording `Func` delegates the same way `SettingsDataPagesFixture.cs` constructs its context (copy the required ctor arguments from there), and constructs `SettingsViewModel` over it

**Interfaces:**
- Consumes: `CompanionFeatureContext.DeleteRemoteDataAsync`, `DeleteLocalDataAsync` (existing), `StopRemoteSyncAsync`, `StartRemoteSyncAsync`, `RemoteDeleteAvailable` (new).
- Produces: `SettingsViewModel.DeleteMyDataAsync(CancellationToken)`, `SettingsViewModel.WipeThisPcOnlyAsync(CancellationToken)`, `IsDeleteConfirmVisible`, `IsWipeThisPcOnlyVisible`, `StatusMessage`.

- [ ] **Step 1: Write failing tests.** `DeleteMyDataHarness.Create(bool remoteDeleteAvailable, bool remoteThrows, bool localThrows = false)` returns `(SettingsViewModel vm, List<string> calls)`; its delegates append `"stop"`, `"remote"`, `"local"`, `"start"` to `calls`; `remoteThrows` makes the remote delegate throw `NotSupportedException` (what the production delegate throws when `!result.Completed`), `localThrows` makes the local delegate throw `IOException`:

```csharp
[Fact]
public async Task Relay_configured_success_stops_sync_then_remote_then_local()
{
    var (vm, calls) = DeleteMyDataHarness.Create(remoteDeleteAvailable: true, remoteThrows: false);

    await vm.DeleteMyDataAsync(TestContext.Current.CancellationToken);

    Assert.Equal(["stop", "remote", "local"], calls);
    Assert.Equal("all cleaned up -- restart dudu to start fresh", vm.StatusMessage);
    Assert.False(vm.HasError);
    Assert.False(vm.IsWipeThisPcOnlyVisible);
}

[Fact]
public async Task Local_failure_is_reported_not_thrown()
{
    var (vm, calls) = DeleteMyDataHarness.Create(remoteDeleteAvailable: false, remoteThrows: false, localThrows: true);

    await vm.DeleteMyDataAsync(TestContext.Current.CancellationToken);   // must not throw

    Assert.True(vm.HasError);
    Assert.False(vm.HasStatus);
}

[Fact]
public async Task Remote_failure_wipes_nothing_restarts_sync_and_offers_wipe_this_pc_only()
{
    var (vm, calls) = DeleteMyDataHarness.Create(remoteDeleteAvailable: true, remoteThrows: true);

    await vm.DeleteMyDataAsync(TestContext.Current.CancellationToken);

    Assert.Equal(["stop", "remote", "start"], calls);
    Assert.DoesNotContain("local", calls);
    Assert.StartsWith("couldn't reach the partner server", vm.ErrorMessage);
    Assert.False(vm.HasStatus);
    Assert.True(vm.IsWipeThisPcOnlyVisible);

    calls.Clear();
    await vm.WipeThisPcOnlyAsync(TestContext.Current.CancellationToken);
    Assert.Equal(["local"], calls);
    Assert.Equal("all cleaned up -- restart dudu to start fresh", vm.StatusMessage);
}

[Fact]
public async Task No_relay_offline_or_safe_mode_wipes_local_only_without_error()
{
    var (vm, calls) = DeleteMyDataHarness.Create(remoteDeleteAvailable: false, remoteThrows: true);

    await vm.DeleteMyDataAsync(TestContext.Current.CancellationToken);

    Assert.Equal(["local"], calls);
    Assert.Equal("all cleaned up -- restart dudu to start fresh", vm.StatusMessage);
}
```

- [ ] **Step 2: Implement** in `SettingsViewModel` (a `FeatureViewModelBase`), routing every delegate through `RunAsync` so nothing escapes into an `async void` XAML handler (`GlobalCrashReporting` does not mark UI exceptions handled — an escape crashes the app):

```csharp
private const string RemoteUnreachableMessage =
    "couldn't reach the partner server, so your partner can still send here. "
    + "try again when you're online, or wipe this pc only";
private const string WipedMessage = "all cleaned up -- restart dudu to start fresh";

public async Task DeleteMyDataAsync(CancellationToken cancellationToken = default)
{
    IsDeleteConfirmVisible = false;
    IsWipeThisPcOnlyVisible = false;
    if (_context.RemoteDeleteAvailable)
    {
        if (!await RunAsync(() => _context.StopRemoteSyncAsync(cancellationToken)))
        {
            return;   // RunAsync already reported the error
        }

        if (!await RunAsync(() => _context.DeleteRemoteDataAsync(cancellationToken)))
        {
            await RunAsync(() => _context.StartRemoteSyncAsync(cancellationToken));
            ReportError(RemoteUnreachableMessage);   // replaces RunAsync's generic error text
            IsWipeThisPcOnlyVisible = true;
            return;
        }
    }

    await WipeThisPcOnlyAsync(cancellationToken);
}

public async Task WipeThisPcOnlyAsync(CancellationToken cancellationToken = default)
{
    if (await RunAsync(() => _context.DeleteLocalDataAsync(cancellationToken), WipedMessage))
    {
        IsWipeThisPcOnlyVisible = false;
    }
}
```

(Never put the exception message in `StatusMessage`; it may contain relay detail.) XAML: "Delete my data" button → confirm panel (reuse the confirm-panel pattern from the deleted `PrivacyDataPage.xaml`: `git show bcf382b:src/Dudu.App/Pages/PrivacyDataPage.xaml`) → `DeleteMyDataAsync`; a "wipe this pc only" button bound to `IsWipeThisPcOnlyVisible` → `WipeThisPcOnlyAsync`. Move the startup checkbox + retry panel from Home to Settings. Update the Settings "what is stored" copy to say revealed partner notes are kept on this pc (incl. automatic backups) until deleted.
- [ ] **Step 3:** Gate G. **Step 4: Commit** — `feat(settings): delete-my-data stops on remote failure; startup toggle on settings`

---

### Task 4: Home page — cute only

**Files:**
- Modify: `src/Dudu.App/ViewModels/HomeViewModel.cs`, `src/Dudu.App/Pages/HomePage.xaml(.cs)`
- Test: delete `tests/Dudu.App.Tests/ViewModels/HomeFocusCountdownTests.cs`; update Home sections of `FeatureViewModelTests.cs`

**Interfaces:**
- Produces: `HomeViewModel` keeps greeting (recipient name) and partner clock text only. The cute buttons stay page-level: `HomePage.xaml.cs` routes them through its `_overlayCommands` (`OverlayCommandRouter`) via `OverlayAction_Click` exactly as today (Task 11 moves tiny hug/breathe onto that handler).

- [ ] **Step 1: Write the failing test** (in `FeatureViewModelTests.cs` Home section or a new `HomeViewModelTests.cs`):

```csharp
[Fact]
public void Home_exposes_no_practical_members()
{
    var names = typeof(HomeViewModel).GetProperties().Select(p => p.Name)
        .Concat(typeof(HomeViewModel).GetMethods().Select(m => m.Name))
        .ToArray();

    foreach (var banned in new[] { "Reminder", "Focus", "Countdown", "CheckIn", "Mood", "Pause", "Task" })
    {
        Assert.DoesNotContain(names, n => n.Contains(banned, StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2:** Remove from `HomeViewModel` + `HomePage.xaml`: status block (pause state, next reminder, active focus, pet state text), pause/resume buttons, "dudu actions" navigation buttons (start focus, open tasks, open love notes, comfort me), comfort sub-panel buttons except tiny hug & breathe, countdowns, mood check-in, startup. Keep a large Dudu frame, greeting, UK clock card, and buttons pet / drink / eat together / tiny hug / breathe with me.
- [ ] **Step 3:** Gate G. **Step 4: Commit** — `refactor(home): keep only dudu, greeting, uk clock and cute buttons`

---

### Task 5: Love notes — reveal keeps the note; opened-notes list; no local jar

**Files:**
- Modify: `src/Dudu.App/ViewModels/LoveNotesViewModel.cs`, `src/Dudu.App/Pages/LoveNotesPage.xaml(.cs)`
- Modify: `src/Dudu.Core/Abstractions/ILocalNoteRepository.cs`, `src/Dudu.Infrastructure/Data/Repositories/LocalNoteRepository.cs` — add `ListRemoteAsync` (ids with `remote-` prefix, `ORDER BY rowid DESC`); keep `SaveToJarAsync`/`DeleteAsync`
- Test: `tests/Dudu.App.Tests/ViewModels/LoveNotesRevealTests.cs` (create); `tests/Dudu.Infrastructure.Tests/Data/LocalNoteRepositoryRemoteListTests.cs` (create)

**Interfaces:**
- Consumes: `ICompanionFeatureTransactions.SaveRemoteNoteAndConsumeEnvelopeAsync(LocalLoveNote note, string messageId, DateTimeOffset nowUtc, CancellationToken)`; `CompanionFeatureContext.DiscardHeldRemoteNoteAsync(string messageId, CancellationToken)`.
- Produces: `ILocalNoteRepository.ListRemoteAsync(CancellationToken) : Task<IReadOnlyList<LocalLoveNote>>`; `LoveNotesViewModel.OpenedNotes` (ObservableCollection<LocalLoveNote>).

- [ ] **Step 1: Infra failing tests** in `LocalNoteRepositoryRemoteListTests.cs`. Copy the private `DatabaseFixture` pattern from `DatabaseTests.cs:1991-2027` (temp root, `Database.OpenAsync(new DatabaseOptions(...))`, dispose + delete); repository = `new LocalNoteRepository(fixture.Database)` (check its real ctor). The repository's write method is `SaveToJarAsync(LocalLoveNote, ct)` (an `ON CONFLICT DO UPDATE` upsert); check `LocalLoveNote`'s real ctor.

```csharp
[Fact]
public async Task ListRemoteAsync_returns_only_remote_prefixed_notes_most_recent_insert_first()
{
    await using var fixture = await DatabaseFixture.CreateAsync();
    var repo = new LocalNoteRepository(fixture.Database);
    var ct = TestContext.Current.CancellationToken;
    await repo.SaveToJarAsync(Note("remote-1", "first"), ct);
    await repo.SaveToJarAsync(Note("seed-1", "seeded"), ct);
    await repo.SaveToJarAsync(Note("remote-2", "second"), ct);

    var remote = await repo.ListRemoteAsync(ct);

    Assert.Equal(["remote-2", "remote-1"], remote.Select(n => n.Id).ToArray());
}
```

- [ ] **Step 2:** Implement `ListRemoteAsync` with `WHERE id LIKE 'remote-%' ORDER BY rowid DESC` (`local_notes` has no timestamp column; an `ON CONFLICT DO UPDATE` upsert keeps its rowid). Remove every other `ILocalNoteRepository` member that loses all callers by the end of Task 8.
- [ ] **Step 3: App failing test** in `LoveNotesRevealTests.cs`. `SettingsDataPagesFixture` has no reveal support today (its `UnusedFeatureTransactions` throws). Extend it with: a configurable `revealRemoteNoteAsync` delegate returning a `RevealedRemoteNote` for a given envelope; a `RecordingFeatureTransactions` implementing `ICompanionFeatureTransactions` that records `SaveRemoteNoteAndConsumeEnvelopeAsync(note, messageId, …)` calls into `ConsumedMessageIds` and saves the note into the fixture's note fake; an in-memory `IRemoteEnvelopeRepository` pre-seeded with one pending envelope; a `discardHeldRemoteNoteAsync` recorder (`DiscardedHeldRemoteNoteIds`); `ListRemoteAsync` on the note fake. The VM's load method is `RefreshAsync` (not `LoadAsync`).

```csharp
[Fact]
public async Task Reveal_consumes_the_envelope_and_adds_to_opened_notes()
{
    var fixture = new SettingsDataPagesFixture();
    var envelope = fixture.AddPendingEnvelope(messageId: "m1", plaintext: "hi love");
    var vm = fixture.CreateLoveNotesViewModel();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.RevealRemoteNoteAsync(envelope, TestContext.Current.CancellationToken);

    Assert.Equal(["m1"], fixture.ConsumedMessageIds);
    Assert.Contains(vm.OpenedNotes, n => n.Id == "remote-m1");
    Assert.DoesNotContain(vm.PendingRemoteNotes, e => e.MessageId == "m1");
    Assert.Equal(["m1"], fixture.DiscardedHeldRemoteNoteIds);
}
```

(Match `SettingsDataPagesFixture`'s real construction style and the VM's real member names; add a second test that deleting an opened note removes it from `OpenedNotes` and the repository.)
- [ ] **Step 4:** In `RevealRemoteNoteAsync`, after `_context.RevealRemoteNoteAsync`, run the body of today's `SaveOpenedNoteAsync` (save+consume, discard held, update lists) and then the dismiss + reaction one-shot. Delete `SaveOpenedNoteAsync`, `ShowLocalNoteAsync`, local draft/editor/include-in-choices/let-dudu-choose members and their XAML. `DeleteLocalNoteAsync` today also calls `DiscardHeldLocalNoteAsync`; for opened (`remote-`) notes drop that call (the held copy was already discarded on reveal) — the `DiscardHeldLocalNoteAsync` member itself goes in Task 8. Opened notes list loads from `ListRemoteAsync`; delete removes via repository delete. Update `docs/release.md` and `PRODUCT.md` privacy wording: every revealed partner note is now stored on this pc (and in automatic pre-migration backups) until deleted from the opened list or via Delete my data.
- [ ] **Step 5:** Gate G. **Step 6: Commit** — `feat(notes): revealing a partner note keeps it; drop local note jar ui`

---

### Task 6: Remove reminders end-to-end (and the presentation heartbeat rewrite)

**Files:**
- Modify: `src/Dudu.App/Hosting/AppHost.cs` — delete `IAppHostReminderService`, `ReminderHostService`, reminder-engine ctor overloads; `RunReminderTickAsync` → `RunPresentationTickAsync` (reconcile → gateway tick); scheduler loop renamed; error op `reminder-scheduler` → `presentation-scheduler`; startup call with `releasePresentations: false` removed — startup only starts the presentation gateway and does **not** reconcile or release (the first timer tick does, as today); resume/unlock catch-up = reconcile + gateway tick
- Delete: `src/Dudu.Core/Reminders/` (all), `src/Dudu.Core/Abstractions/IReminderRepository.cs`, `IReminderDueSink.cs`, `IReminderWriter.cs`, `src/Dudu.Core/Models/Reminder.cs` and `RecurrenceRule` (and occurrence types), `src/Dudu.Infrastructure/Data/Repositories/ReminderRepository.cs`, `src/Dudu.App/Presentation/ReminderDueSink.cs`, `src/Dudu.App/Notifications/ReminderToastActions.cs`
- Modify: `src/Dudu.Core/Abstractions/IAppUnitOfWork.cs`, `src/Dudu.Infrastructure/Data/AppUnitOfWork.cs`, `src/Dudu.Infrastructure/DependencyInjection.cs`
- Modify: `src/Dudu.Infrastructure/Data/CompanionFeatureTransactionService.cs` + `ICompanionFeatureTransactions` — delete `SavePreferencesAndDefaultRemindersAsync`, `RestorePreferencesAndDefaultRemindersAsync`; keep `SaveRemoteNoteAndConsumeEnvelopeAsync`. Callers that saved preferences via it switch to the plain preferences repository save.
- Modify: `src/Dudu.App/Presentation/PresentationCoordinator.cs`, `PresentationPolicy.cs`, `DurableNotification` (wherever defined) — remove `PresentationItemKind.Reminder`, `DurableNotification.Reminder`, `IsRoutine`, `IgnoresQuietHours`, reminder branches; legacy held-row purge
- Modify: `src/Dudu.Core/Pet/PetEvent.cs`, `PetStateMachine.cs`, `src/Dudu.Core/Models/PetPresentation.cs` — remove `ReminderDue`, `PetState.Reminder`
- Modify: `src/Dudu.App/Audio/AudioCueSelection.cs` (+ `AudioCueEvent`) — remove `Reminder`
- Modify: `src/Dudu.App/Notifications/NotificationActivation.cs`, `NotificationInvocationRouter.cs`, `AppNotificationService.cs` (`ShowReminderAsync`), `INotificationService`
- Modify: `src/Dudu.App/Hosting/WindowsCompanionProductionComposition.cs`, `WindowsCompanionBootstrap.cs`, `AppLifecycleCoordinator.cs` (resume catch-up tick calls the presentation tick), `src/Dudu.App/Windows/SettingsWindow.xaml.cs` (`PetState.Reminder` uses)
- **Rewrite: `tests/Dudu.Infrastructure.Tests/Hosting/AppHostTests.cs`** — it compiles the linked `AppHost.cs` and runs in Gate G on Linux; delete `TestReminderService` and every reminder assertion (e.g. `Reminder.TickCount`), switch `AppHostFixture` to the reminder-free ctor, and keep every non-reminder assertion (db-init ordering, gateway start/dispose, remote-sync start/dispose, error reporting)
- Modify: `src/Dudu.App/ViewModels/CompanionFeatureContext.cs`, `OnboardingViewModel.cs` (remove `LocalReminderDefaults` writes; keep the rest until Task 11)
- Modify: `tests/Dudu.WindowsHarness/Program.cs`, `tests/Dudu.WindowsHarness/LongRunScenario.cs` — remove reminder scenarios and reminder-occurrence assertions; keep `DatabaseOptions(paths.Database, paths.Backups)`
- Delete tests: `tests/Dudu.Core.Tests/Reminders/*`, `tests/Dudu.Core.Tests/Models/RecurrenceRuleTests.cs`, `tests/Dudu.App.Tests/Presentation/ReminderDueSinkTests.cs`, `ReminderBubbleUxTests.cs`, `tests/Dudu.App.Tests/Notifications/ReminderToastActionTests.cs`; update `PresentationCoordinatorTests.cs`, `PresentationPolicyTests.cs`, `PresentationHeldQueuePersistenceTests.cs`, `SinkDiagnosticsTests.cs`, `NotificationColdStartTests.cs`, `ChromeWiringUxTests.cs`, `ProductionStartupContractTests.cs`, `AppHostTests.cs`, `PetStateMachineTests.cs`, `PetInteractionStateTests.cs`, `tests/Dudu.Infrastructure.Tests/DependencyInjectionTests.cs`, `Security/PrivacyBoundaryTests.cs`, `Data/CompanionFeatureTransactionTests.cs`
- Test (new): new cases in `tests/Dudu.Infrastructure.Tests/Hosting/AppHostTests.cs` (runs on Linux) and in `tests/Dudu.App.Tests/Notifications/NotificationActivationTests.cs` (linked into and run by Infra tests on Linux); router/cold-start cases in `tests/Dudu.App.Tests/Notifications/*`; new cases in `PresentationHeldQueuePersistenceTests.cs`

**Interfaces:**
- Produces: `AppHost` constructors without any reminder parameter; `PresentationItemKind { RemoteNote }` after Task 8 (this task removes `Reminder`); `NotificationActivationAction { OpenNote }` only, record without `ReminderId`/`ActsInBackground`; `NotificationActivation.TryParse` returns `null` for `reminder-*` actions (same as unknown actions); `Destination` only returns `"notes"`; `INotificationService` without `ShowReminderAsync`.

- [ ] **Step 1: Failing tests**

In `tests/Dudu.Infrastructure.Tests/Hosting/AppHostTests.cs` (replace the reminder-based gateway/reconcile tests at ~lines 170-270; `TestPresentationGateway`/`TestVisibilityReconciler` lose their reminder-count params — record call order into a shared list instead):

```csharp
[Fact]
public async Task Startup_starts_the_gateway_without_reconciling_or_releasing()
{
    using var fixture = new AppHostFixture();
    var calls = new List<string>();
    fixture.Host.AttachPresentationGateway(new TestPresentationGateway(calls));
    fixture.Host.AttachVisibilityReconciler(new TestVisibilityReconciler(calls));

    await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

    Assert.Equal(["start"], calls);
    await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
}

[Fact]
public async Task Timer_signal_reconciles_then_ticks_the_gateway()
{
    using var fixture = new AppHostFixture();
    var calls = new List<string>();
    var gateway = new TestPresentationGateway(calls);
    fixture.Host.AttachPresentationGateway(gateway);
    fixture.Host.AttachVisibilityReconciler(new TestVisibilityReconciler(calls));
    await fixture.Host.StartAsync(TestContext.Current.CancellationToken);

    fixture.TimerFactory.Timer.Signal();
    await gateway.Ticked.Task.WaitAsync(TestContext.Current.CancellationToken);

    Assert.Equal(["start", "reconcile", "tick"], calls);
    await fixture.Host.StopAsync(TestContext.Current.CancellationToken);
}
```

Plus: `ResumeAsync` produces `reconcile`, `tick`; a throwing gateway tick is reported under `presentation-scheduler` (adapt the existing error-report test).

In `tests/Dudu.App.Tests/Notifications/NotificationActivationTests.cs` (linked into Infra tests, so it runs on Linux) — copy the literal argument keys from `NotificationActivation.Resolve` / `ReminderToastActions.cs` before deleting them, and cover both `TryParse` overloads:

```csharp
[Theory]
[InlineData("action=reminder-done&reminderId=r1")]
[InlineData("action=reminder-snooze&reminderId=r1")]
[InlineData("action=open-reminder&reminderId=r1")]
public void Legacy_reminder_toast_actions_parse_to_null(string arguments)
{
    Assert.Null(NotificationActivation.TryParse(arguments));
}
```

Add a `NotificationInvocationRouter` test and a cold-start test (model on `NotificationColdStartTests.cs`): each legacy argument throws nothing, navigates nowhere, and calls no feature delegate.

In `PresentationHeldQueuePersistenceTests.cs`, using the file's `RecordingHeldPresentationRepository` (`Seed(HeldPresentation)`, `.Rows` is a dictionary) and its existing coordinator/error-reporter construction:

```csharp
// PresentationHeldQueuePersistenceTests.cs — new cases
[Theory]
[InlineData("Reminder")]
[InlineData("LocalNote")]
public async Task Legacy_held_kinds_are_deleted_silently_and_remote_notes_still_load(string legacyKind)
{
    var repo = new RecordingHeldPresentationRepository();
    repo.Seed(/* HeldPresentation with Kind = legacyKind, Key = "legacy-1" — use the file's existing construction */);
    repo.Seed(/* HeldPresentation with Kind = "RemoteNote", Key = a GUID */);
    // build the coordinator paused, with the file's recording error reporter, exactly as neighbouring tests do

    await coordinator.StartAsync(TestContext.Current.CancellationToken);

    Assert.DoesNotContain(repo.Rows.Values, r => r.Kind == legacyKind);
    Assert.Single(repo.Rows.Values, r => r.Kind == "RemoteNote");
    Assert.Empty(errorReports);   // no presentation-held-load report
}
```

- [ ] **Step 2:** Implement. In `PresentationCoordinator.LoadHeldItemsAsync`, before `ToDurableNotification`, add:

```csharp
private static readonly HashSet<string> RetiredHeldKinds = new(StringComparer.Ordinal) { "Reminder", "LocalNote" };
// in the load loop:
if (RetiredHeldKinds.Contains(record.Kind))
{
    await RemoveHeldAsync(record.Key, cancellationToken);   // silent: retired feature, not a failure
    continue;
}
```

`NotificationActivation`: delete `ReminderDone`, `ReminderSnooze`, `OpenReminder`, `ReminderId`, `ActsInBackground`; `Resolve` returns `null` for those action strings (they fall into the existing unknown-action arm); `Destination` only returns `"notes"`. Remove the reminder branches in `NotificationInvocationRouter` and `App.xaml.cs` cold start (null activation is already a no-op there). `PresentationPolicy.Decide` keeps a `nowUtc` default if it has one today.
- [ ] **Step 3:** Delete reminder code and tests per the file list. `grep -rn "Reminder" src tests --include='*.cs' --include='*.xaml' | grep -v /obj/` should leave only: `RetiredHeldKinds`, legacy-activation tests, and legacy preference column mapping if any.
- [ ] **Step 4:** Gate G. **Step 5: Commit** — `refactor: remove reminders; app host drives a plain presentation tick`

---

### Task 7: Remove tasks and focus; introduce the Busy suppression input

**Files:**
- Delete: `src/Dudu.App/ViewModels/FocusDisplay.cs` (moved there in Task 2; Home stops using it in Task 4) and `tests/Dudu.App.Tests/ViewModels/FocusDisplayTests.cs`; `src/Dudu.Core/Tasks/`, `src/Dudu.Core/Focus/`, `src/Dudu.Core/Models/TaskItem.cs`, `FocusSession.cs`, their repository interfaces; `src/Dudu.Infrastructure/Data/Repositories/TaskRepository.cs`, `FocusSessionRepository.cs`
- Modify: `IAppUnitOfWork`, `AppUnitOfWork`, `DependencyInjection.cs`, `CompanionFeatureContext.cs`, `WindowsCompanionProductionComposition.cs` (remove `FocusService.SessionExpired` handler ~line 417, startup focus re-latch ~line 1236 and its `focus-restore` op), `SettingsWindow.xaml.cs` (FocusService use)
- Modify: `src/Dudu.Core/Pet/PetEvent.cs`, `PetStateMachine.cs`, `PetPresentation.cs` — remove `FocusStarted`, `FocusEnded`, `PetState.Focus`, `PetState.FocusTransition`, `IsFocusActive`; `IsQuietCompanyActive` → eating only
- Modify: `src/Dudu.App/Presentation/PresentationCoordinator.cs` — `SuppressionSnapshot.FocusActive` → `Busy`, computed `_pet.IsEatingActive || _pet.IsDragging`; `PresentationPolicy.Decide(... focusActive ...)` → `busy`; `PetActivityGate` / `PetActivityDirector` same rename
- Delete tests: `tests/Dudu.Core.Tests/Tasks/*`, `Focus/*`; update `PetStateMachineTests`, `PetInteractionStateTests`, `PetPresentationCoordinatorTests`, `PresentationCoordinatorTests`, `PresentationPolicyTests`, `PetActivityDirectorTests`, `ComfortBreathingInterruptTests`, `OverlayTestFeatureContext`, `SettingsDataPagesFixture`, `ConnectionViewModelForgetPairingTests`
- Test (new): cases in `PresentationCoordinatorTests.cs`, `PetStateMachineTests.cs`

**Interfaces:**
- Produces: `PresentationPolicy.Decide(bool fullscreen, bool paused, bool sessionLocked, bool busy, DateTimeOffset now, bool recordRelease, bool userHidden)` (quiet parameter removed in Task 10 — until then keep `nowQuiet` first); `PetStateMachine.IsEatingActive`, `IsDragging` unchanged.

- [ ] **Step 1: Failing tests** in `PresentationCoordinatorTests.cs`, built with the file's real ctor pattern (see the ambient test there) including a **due** `AmbientScheduler` (clock advanced past the interval) and an affection tracker primed for a tantrum (reuse the existing tantrum test setup):

```csharp
[Theory]
[InlineData("eating")]
[InlineData("dragging")]
public async Task Busy_pet_suppresses_ambient_and_tantrum_and_holds_a_remote_note(string busyKind)
{
    var pet = PetStateMachine.CreateIdle();
    if (busyKind == "eating") pet.Handle(new PetEvent.EatingStarted("meal-1"));
    else pet.Handle(/* the real drag-start event in PetEvent.cs */);
    // coordinator: due ambient scheduler, tantrum-ready affection tracker, recording play delegate `played`,
    // policy with TimeSpan.Zero interval

    await coordinator.TickAsync(TestContext.Current.CancellationToken);
    Assert.DoesNotContain(played, p => p.State is PetState.Ambient);
    Assert.DoesNotContain(played, p => /* REQUIRED: replace with the tantrum predicate from the existing tantrum test; `false` makes this vacuous */ false);

    await coordinator.PublishAsync(DurableNotification.RemoteNote(Guid.NewGuid().ToString("D")),
        bypassSuppression: false, TestContext.Current.CancellationToken);
    await coordinator.TickAsync(TestContext.Current.CancellationToken);
    Assert.Equal(1, policy.QueuedCount);
}
```

Plus a Core test in `PetStateMachineTests`: during eating, `RemoteNoteArrived` keeps the eating presentation and holds the note card (mirror the existing focus/eating test, focus → eating).
- [ ] **Step 2:** Implement and delete per file list.
- [ ] **Step 3:** Gate G. **Step 4: Commit** — `refactor: remove tasks and focus; eating/dragging drive busy suppression`

---

### Task 8: Local notes → standalone ambient with a daily cap

**Files:**
- Delete: `src/Dudu.Core/Notes/LocalNoteSelector.cs`
- Modify: `src/Dudu.Core/Abstractions/ILocalNoteRepository.cs`, `LocalNoteRepository.cs` — remove selector-only members (`ListEnabledAsync`, `CountUnsolicitedShownAsync`, `GetMostRecentShownIdsAsync`, shown-history writes) that lose all callers
- Modify: `src/Dudu.Core/Pet/AmbientScheduler.cs` — remove `idle` from the ambient pool; the no-stickers fallback (`AmbientScheduler.cs:116-125`, currently returns `idle`) returns `null`
- Modify: `src/Dudu.App/Presentation/PresentationCoordinator.cs` — drop `localNoteSelector` ctor param and the scheduler/selector invariant; remove `PresentationItemKind.LocalNote`, `PresentationItemKind.Ambient`, `DurableNotification.LocalNote`; ambient presented directly (mirror `PresentTantrumAsync`) with a per-local-day cap of 3 counting only played moments; call `_policy.RecordImmediateRelease(...)` after an ambient moment plays exactly as today's ambient-note path does
- Modify: `src/Dudu.App/Presentation/PresentationPolicy.cs` — remove the `Ambient` kind's test-only factory
- Modify: `src/Dudu.App/Audio/AudioCueSelection.cs` — add `ForAmbient(string animationKey)`: `sticker-*` → `Sticker`, else `ManualInteraction`, at `Background` priority (today's `LocalNote` arm of `ForNotification`); delete the `LocalNote` arm
- Modify: `src/Dudu.Infrastructure/Data/Database.cs` (~line 409) and `LocalDataMaintenanceService.cs` (~line 103) — stop calling note seeding; delete `SeedData` note seeding (leave `seed_state` table dormant, still wiped)
- Modify: `CompanionFeatureContext.cs`, composition, `DependencyInjection.cs`
- Delete/update tests: `tests/Dudu.Core.Tests/Companion/CompanionFeatureTests.cs` (selector parts), `tests/Dudu.Infrastructure.Tests/Data/SeedDataTests.cs` (delete or reduce to "no seeding happens"), `PresentationCoordinatorTests.cs` (replace the local-note ambient test), `AmbientSchedulerTests.cs`, `AudioCueSelection` tests, `PresentationPolicyTests.cs` (Ambient factory)

**Interfaces:**
- Produces: `PresentationCoordinator` ctor without `LocalNoteSelector`; `internal const int MaxUnsolicitedAmbientPerDay = 3;`; `AudioCueSelection.ForAmbient(string)`; `AmbientScheduler.TryGetNextEvent` never yields `idle`.

- [ ] **Step 1: Failing tests** (replace `Eligible_ambient_tick_selects_and_presents_a_local_note_through_the_gateway`):

```csharp
[Fact]
public async Task Eligible_ambient_tick_presents_a_sticker_with_no_note_text()
{
    var clock = new FixedClock(DateTimeOffset.Parse("2026-09-14T16:00:00Z"));
    var scheduler = new AmbientScheduler(clock, new FixedRandomSource(), Preferences.Default.AmbientMinimumInterval);
    PetPresentation? presented = null;
    var coordinator = new PresentationCoordinator(
        new PresentationPolicy(TimeSpan.Zero),
        new RecordingNotificationService(),
        PetStateMachine.CreateIdle(),
        (presentation, _, _) => { presented = presentation; return Task.CompletedTask; },
        () => AnimationOptions.Default,
        pauseState: () => PauseState.None,
        petGate: new SemaphoreSlim(1, 1),
        ambientScheduler: scheduler,
        utcNow: () => clock.UtcNow,
        availableStickerKeys: ["sticker-001"]);

    clock.Advance(Preferences.Default.AmbientMinimumInterval);
    await coordinator.TickAsync(TestContext.Current.CancellationToken);

    Assert.NotNull(presented);
    Assert.Equal(PetState.Ambient, presented.State);
    Assert.True(string.IsNullOrEmpty(presented.BubbleBody));
}
```

Also write these fully, using the same setup (match the real ctor/param names in the file; if `isQuietHours` is still required because Task 10 has not run, pass `isQuietHours: () => false`):
  - `Ambient_is_capped_at_three_played_moments_per_local_day_and_resets_next_day` — `List<PetPresentation>` recorder; 10 × (`clock.Advance(interval * 2)`; `await TickAsync`) → `Assert.Equal(3, played.Count(p => p.State == PetState.Ambient))`; `clock.Advance(TimeSpan.FromDays(1))`, one more advance+tick → 4.
  - `Suppressed_or_failed_ambient_does_not_count_toward_the_cap` — make the play delegate throw (or pause) for the first 2 eligible ticks, then allow; expect 3 played moments afterwards on the same day.
  - `Held_remote_note_waits_the_silent_interval_after_an_ambient_moment` — policy with a non-zero minimum interval; play an ambient moment; publish a remote note while it would otherwise release; assert it stays queued until the interval passes (mirror today's ambient-note/`RecordImmediateRelease` test in this file).
  - `AudioCueSelection` tests: `ForAmbient("sticker-003")` → `Sticker`, `ForAmbient("drink")`/`("blink")` → `ManualInteraction`, all at `Background` priority.
  - `AmbientSchedulerTests`: over many seeds the pool never yields `idle`; a pack with no sticker keys and a random pick that would hit the fallback yields `null`.
- [ ] **Step 2: Implement** in `TickAsync` after the tantrum check:

```csharp
if (_ambientScheduler is null || IsSuppressed(environment)) return;
var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _localTimeZone).DateTime);
lock (_gate)
{
    if (_ambientDay != today) { _ambientDay = today; _ambientShownToday = 0; }
    if (_ambientShownToday >= MaxUnsolicitedAmbientPerDay) return;
}
var ambient = _ambientScheduler.TryGetNextEvent(/* existing args minus quiet/focus */, _availableStickerKeys);
if (ambient is not PetEvent.AmbientRequested request) return;
if (await PresentAmbientAsync(request, cancellationToken))
{
    lock (_gate) { _ambientShownToday++; }
    _policy.RecordImmediateRelease(now);   // same call/args as today's ambient-note path
}
```

`PresentAmbientAsync` mirrors `PresentTantrumAsync` (same pet gate, `PlayWithTimeoutAsync`, returns true only when the moment played; audio via `AudioCueSelection.ForAmbient(request.AnimationKey)`). `_localTimeZone` comes from a new optional ctor param `TimeZoneInfo? localTimeZone = null` defaulting to `TimeZoneInfo.Local`. Every ambient test passes `localTimeZone: TimeZoneInfo.Utc` so results don't depend on the machine's zone.
- [ ] **Step 3:** Delete per file list; Gate G. **Step 4: Commit** — `refactor: ambient plays on its own (3/day, no idle); drop local note selector and seeding`

---

### Task 9: Remove countdowns and mood check-ins

**Files:**
- Delete: `src/Dudu.Core/Countdowns/`, `src/Dudu.Core/CheckIns/`, `src/Dudu.Core/Models/MoodCheckIn.cs`, `Countdown` model, their repository interfaces; `src/Dudu.Infrastructure/Data/Repositories/CountdownRepository.cs`, `CheckInRepository.cs`
- Modify: `IAppUnitOfWork`, `AppUnitOfWork`, `DependencyInjection.cs`, `CompanionFeatureContext.cs`, composition, `HomeViewModel.cs` (any leftovers)
- Delete tests: `tests/Dudu.Core.Tests/Countdowns/*`, `CheckIns/*`; update `CompanionFeatureTests.cs`, `DependencyInjectionTests.cs`, fixtures

- [ ] **Step 1:** Add to `tests/Dudu.Infrastructure.Tests/DependencyInjectionTests.cs`:

```csharp
[Theory]
[InlineData("Dudu.Core.Countdowns.CountdownService")]
[InlineData("Dudu.Core.CheckIns.CheckInService")]
[InlineData("Dudu.Core.Reminders.ReminderEngine")]
[InlineData("Dudu.Core.Focus.FocusService")]
[InlineData("Dudu.Core.Tasks.TaskService")]
[InlineData("Dudu.Core.Notes.LocalNoteSelector")]
public void Removed_feature_types_no_longer_exist(string typeName)
{
    Assert.Null(typeof(Dudu.Core.Models.Preferences).Assembly.GetType(typeName));
}
```

- [ ] **Step 2:** Delete per file list. Gate G. **Step 3: Commit** — `refactor: remove countdowns and mood check-ins`

---

### Task 10: Remove quiet hours

**Files:**
- Delete: `src/Dudu.Core/Policies/QuietHoursPolicy.cs`, `tests/Dudu.Core.Tests/Policies/QuietHoursPolicyTests.cs`
- Modify: `src/Dudu.Core/Pet/AmbientScheduler.cs` (drop `QuietHours` ctor param and `quietHoursOverride`), `PetActivityScheduler.cs`, `AffectionTracker.cs`, `PetStateMachine.cs` (any quiet refs), `src/Dudu.App/Audio/AudioCueService.cs`, `src/Dudu.App/Hosting/AppLifecycleCoordinator.cs` (welcome-back quiet delegate), `src/Dudu.App/Presentation/PresentationCoordinator.cs` (`isQuietHours` ctor param, `NowQuiet`), `PresentationPolicy.cs` (`nowQuiet` param), `WindowsCompanionBootstrap.cs`, composition, `OnboardingViewModel.cs`/`OnboardingPage.xaml(.cs)` (quiet step — fully removed in Task 11 but must compile here), `XamlCompileStubs.cs`, `SettingsWindow.xaml.cs`
- Update tests: every `isQuietHours:` argument in `tests/Dudu.App.Tests/**` (remove), `AmbientSchedulerTests`, `PetActivitySchedulerTests`, `AffectionTrackerTests`, `AudioCueServiceTests`, `AppLifecycleCoordinatorTests`, `PresentationPolicyTests`

**Interfaces:**
- Produces: `PresentationPolicy.Decide(bool fullscreen, bool paused, bool sessionLocked, bool busy, DateTimeOffset now, bool recordRelease, bool userHidden)`; `AmbientScheduler(IClock clock, IRandomSource random, TimeSpan minimumInterval)`; `PresentationCoordinator` ctor without `isQuietHours`.

- [ ] **Step 1:** Add to `tests/Dudu.Core.Tests/Pet/AmbientSchedulerTests.cs`:

```csharp
[Fact]
public void Ambient_is_eligible_at_night_when_nothing_else_suppresses()
{
    var clock = new FakeClock(DateTimeOffset.Parse("2026-09-14T23:30:00Z"));   // AmbientSchedulerTests' clock fake
    var scheduler = new AmbientScheduler(clock, new FixedRandomSource(), TimeSpan.FromMinutes(15));
    clock.Advance(TimeSpan.FromMinutes(16));

    var next = scheduler.TryGetNextEvent(paused: false, busy: false, fullscreen: false, sessionLocked: false, ["sticker-001"]);

    Assert.IsType<PetEvent.AmbientRequested>(next);
}
```

(Adjust the `TryGetNextEvent` parameter list to the post-change signature. This test already passes today because default quiet hours are disabled — it is a regression guard, not a red test; the red signal for this task is the compile break from removing the quiet parameters, and the `grep` below.)
- [ ] **Step 2:** Implement. `grep -rni "quiet" src --include='*.cs' --include='*.xaml' | grep -v /obj/` → only `IsQuietCompanyActive` (eating) and the Preferences repository's fixed legacy-column INSERT values remain.
- [ ] **Step 3:** Gate G. **Step 4: Commit** — `refactor: remove quiet hours`

---

### Task 11: Onboarding → 3 steps; tray → 4 fixed items; overlay bubble removed (click-to-pet kept); breathe on Home; remove global hotkey

**Files:**
- Modify: `src/Dudu.App/ViewModels/OnboardingViewModel.cs`, `src/Dudu.App/Pages/OnboardingPage.xaml(.cs)` — steps Name → Look → Pairing; `CompleteAsync` saves profile, preferences (`LaunchAtSignIn = true`, `HidePetDuringFullscreen = true` as today), default placement; no `LocalReminderDefaults`
- Modify: `src/Dudu.App/Tray/TrayIconService.cs` — `TrayCommand { ShowOrHide, PauseOneHourOrResume, OpenSettings, Exit }`; the fixed `Commands` list keeps 4 entries in every state (item id = index + 1 on `WM_COMMAND`); item 2's label is "Pause 1 hour" when not paused, "Resume" when paused (same pattern as today's `PauseIndefinitelyOrResume`)
- Modify: `src/Dudu.App/Hosting/CompanionCommandRouter.cs` — `PauseOneHourOrResume`: paused (any mode, incl. legacy persisted) → resume via the existing resume branch (~lines 169-179); else `PausePolicy.ForOneHour`. `PausePolicy.cs`: delete `ForFiveMinutes`/`UntilTomorrowAtSeven` only if no callers remain; `PauseMode` + `PausePersistence` unchanged
- Delete: `src/Dudu.App/Overlay/OverlayActionSurfaceController.cs`; `ActionBubbleLayout.cs` — first move the `OverlayAction` and `ComfortAction` enums into a new `src/Dudu.App/Overlay/OverlayActions.cs`, and move `Label`, `ComfortLabel`, `AutomationId`, `ComfortAutomationId` into `OverlayCommandRouter` as static helpers (update callers: `HomePage.xaml.cs`, `SettingsWindow.xaml.cs` header, tests); then delete `ActionBubbleLayout.cs` with the remaining geometry, `OverlayActionSurfaceKind` and `ComfortActions` list; action/comfort drawing in `OverlaySurfaceRenderer` and `SkiaFrameComposer` (`SetActionSurface`, ~lines 35, 90-98, 195-197); surface hit-test/pointer/viewport branches in `OverlayWindowHost` (~lines 327, 770, 1390-1416)
- Modify: `src/Dudu.App/Overlay/OverlayActionDispatchQueue.cs` — delete the two `Enqueue(surface, …)` overloads; `EnqueuePet(Func<CancellationToken, Task> petAsync)` keeps the queue's serialization and error reporting
- Modify: `src/Dudu.App/Overlay/OverlayWindowHost.cs` — `SetActionSurfaceAsync` → `SetPetHandlerAsync(Func<CancellationToken, Task>)`; body click (~line 1175) calls `EnqueuePet(_petHandler)` (no-op if unset)
- Modify: composition — delete `new OverlayActionSurfaceController()` (~line 284), `composer.SetActionSurface` (~496), the `ActionSurface` property (~line 73) and its initializer (~1107), `actionSurface.Bind(overlayRouter)` (~1089). The overlay-start lambda (~834) and `var overlayRouter` (~1086) are in the same `try` block, so referencing the later local inside the earlier lambda is CS0841: declare `OverlayCommandRouter? overlayRouter = null;` where `actionSurface` was declared (~284), assign it at ~1086 (`overlayRouter = new OverlayCommandRouter(...)`), and wire `overlay.SetPetHandlerAsync(ct => overlayRouter?.ExecuteAsync(OverlayAction.Pet, ct) ?? Task.CompletedTask, cancellationToken)`. Idle-activity busy check (~771) becomes `Busy: pet.Current.State != PetState.Idle`
- Modify: `src/Dudu.App/Overlay/OverlayCommandRouter.cs` — `OverlayAction { Pet, DrinkWater, EatTogether, TinyHug, BreatheWithMe }`, `ComfortAction { BreatheWithMe, Close }`; `ComfortPanelState` stays. **Add real arms** (every switch ends in `_ => throw`, so a missing arm compiles and then throws at runtime on Windows only): `ExecuteAsync` — `OverlayAction.TinyHug => PresentTinyHugAsync(ct)`, `OverlayAction.BreatheWithMe => BreatheWithMeAsync(ct)`; the same two in `ExecuteAccessibleAsync`; `EquivalentSettingsDestination(OverlayAction)` — `TinyHug`/`BreatheWithMe` → `"home"`; `Label`/`AutomationId` arms for both. Remove `LoveNote`, `ComfortMe`, `ComfortAction.TinyHug/ReadALoveNote/TakeAFiveMinuteBreak` arms and `TakeFiveMinuteBreakAsync`/`ExecuteComfortAsync(CancellationToken)` (the comfort-menu opener) once unreferenced
- Modify: `src/Dudu.App/Pages/HomePage.xaml(.cs)` — the tiny hug and breathe buttons use `Click="OverlayAction_Click"` with Tags `TinyHug` / `BreatheWithMe` (today they use `ComfortAction_Click`, whose `Enum.TryParse<ComfortAction>("TinyHug")` would silently fail after the enum shrinks); remove the comfort sub-panel
- Modify: `src/Dudu.App/Pages/HomePage.xaml(.cs)` (the router lives only on the page as `_overlayCommands`, `HomePage.xaml.cs:13`; `HomeViewModel` has no router) — add a small `BreathingPanelPresenter` class in `src/Dudu.App/ViewModels/` that takes the `OverlayCommandRouter`, subscribes to `ComfortPanelChanged`, exposes `IsVisible`, `Instruction`, `PhaseText`, and `StopAsync()` (sends `ComfortAction.Close`); `HomePage` owns one, binds a breathing panel + **Stop** button to it, disposes/unsubscribes on unload and, only if `router.IsBreathing`, stops breathing (a blanket `ComfortAction.Close` would present `Dismissed("comfort")` and cut a running tiny hug short, since tiny hug uses the same `"comfort"` id). The breathe click handler treats `OperationCanceledException` (incl. `TaskCanceledException`) as a normal stop — no error status. Status labels come from the moved `OverlayCommandRouter` helpers
- Delete: `src/Dudu.App/System/GlobalHotkeyService.cs`; wiring in `WindowsCompanionBootstrap.cs`, persisted-shortcut restore, `hotkey` native callback, `SettingsViewModel` shortcut members and XAML
- Delete tests: `tests/Dudu.App.Tests/System/GlobalHotkeyServiceTests.cs`, `HotkeyUxTests.cs`, `Hosting/PersistedShortcutRegistrationTests.cs`, `ViewModels/AppearanceShortcutPersistenceTests.cs`, `tests/Dudu.Infrastructure.Tests/Data/GlobalShortcutPreferenceTests.cs`, `tests/Dudu.Core.Tests/Models/PreferencesGlobalShortcutTests.cs`, every `OverlayActionSurfaceController`/`ActionBubbleLayout` geometry/renderer-bubble **test method** (find with `grep -rn "OverlayActionSurfaceController\|ActionBubbleLayout" tests`; mixed files such as `FeatureViewModelTests.cs` (~1181-1337, 1995-2036, 2451-2494) and `OverlayCommandRouterTests.cs` keep their other tests — delete methods, not files); update `ChromeDiagnosticsTests.cs`, `OnboardingViewModelTests.cs`, `OverlayCommandRouterTests.cs`, `ComfortBreathingInterruptTests.cs`, `Tray/*Tests.cs`, `PausePolicyTests.cs`, `SkiaFrameComposer`/`OverlayWindowHost` tests
- Test (new): `tests/Dudu.App.Tests/System/PauseLegacyRestoreTests.cs`, `tests/Dudu.App.Tests/Tray/TrayMenuFixedItemsTests.cs`, `tests/Dudu.App.Tests/Overlay/OverlayPetClickTests.cs`, `tests/Dudu.App.Tests/ViewModels/BreathingPanelPresenterTests.cs`; update `tests/Dudu.App.Tests/Ui/XamlContractTests.cs` (~lines 264-283: every `AccessiblePrimaryActions` automation ID must appear in `HomePage.xaml`; drop `AccessibleComfortActions`/`DuduComfortButton` checks with the comfort list). `OverlayCommandRouter.PrimaryActions`/`AccessiblePrimaryActions` become `[Pet, DrinkWater, EatTogether, TinyHug, BreatheWithMe]`; TinyHug/BreatheWithMe keep today's Home automation IDs `OverlayComfortActionTinyHug` / `OverlayComfortActionBreatheWithMe` (so UiTests keep working); delete `AccessibleComfortActions` and anything built on `ComfortActions`; tiny-hug router test in `OverlayCommandRouterTests.cs`, onboarding step test in `OnboardingViewModelTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
public sealed class PauseLegacyRestoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T10:00:00Z");

    [Fact]
    public void Every_legacy_mode_restores_exactly_as_before()
    {
        foreach (var mode in Enum.GetValues<PauseMode>())
        {
            var original = mode switch
            {
                PauseMode.None => PauseState.None,
                _ => new PauseState(mode, mode is PauseMode.Indefinite or PauseMode.UntilFullscreenEnds ? null : Now.AddHours(1)),
            };
            var (persistedMode, expires) = PausePersistence.ToPreference(original);
            var restored = PausePersistence.FromPreference(persistedMode, expires, Now);

            var expected = mode == PauseMode.UntilFullscreenEnds ? PauseState.None : original;
            Assert.Equal(expected, restored);
        }
    }
}
```

(Record the actual pre-change outcomes first by running this test against the unmodified `PausePolicy` on Windows CI or by reading `FromPreference`; the assertion must encode today's behavior, whatever it is — adjust `expected` to match the code at the start of this task, not the other way round.)

Onboarding (in `OnboardingViewModelTests.cs`, constructing the VM exactly as that file already does): the VM has exactly 3 steps in order Name → Look → Pairing (assert via its existing step count/index/title members), and `CompleteAsync` saves `LaunchAtSignIn = true`, `HidePetDuringFullscreen = true` and writes no reminders.

Also write fully (model each on the existing tests for that class; match real member names):
  - `TrayMenuFixedItemsTests` — for `PauseState.None` and for each paused `PauseMode`: the built menu has exactly 4 items; item ids 1..4 resolve to `ShowOrHide`, `PauseOneHourOrResume`, `OpenSettings`, `Exit`; item 2's label is "Pause 1 hour" / "Resume". Router: `PauseOneHourOrResume` while unpaused applies a one-hour pause; while paused (incl. a legacy `UntilTomorrowAtSeven` state) resumes.
  - `OverlayPetClickTests` — a dispatch queue with a recording pet handler: `EnqueuePet(handler)` invokes it once and serializes with other queued work; a throwing handler is reported, not rethrown. Plus a router-level check that the handler wired as `ct => router.ExecuteAsync(OverlayAction.Pet, ct)` plays the petted reaction (use the existing `OverlayCommandRouterTests` fakes).
  - `BreathingPanelPresenterTests` — over a real `OverlayCommandRouter` with the `OverlayCommandRouterTests` fakes and a short breathing delay: starting `ExecuteAsync(OverlayAction.BreatheWithMe)` makes `IsVisible` true with non-empty `PhaseText`; `StopAsync()` sends `ComfortAction.Close`, `IsVisible` becomes false, and awaiting the breathing task yields `OperationCanceledException` (which the page treats as a normal stop).
  - Tiny hug — `ExecuteAsync(OverlayAction.TinyHug)` plays the tiny-hug presentation (same assertion as today's `ComfortAction.TinyHug` test), and `EquivalentSettingsDestination(OverlayAction.TinyHug)`/`(BreatheWithMe)` return `"home"`.

- [ ] **Step 2:** Implement per file list. Afterwards `grep -rn "OverlayActionSurfaceController\|actionSurface\|SetActionSurface" src tests --include='*.cs' | grep -v /obj/` → no hits.
- [ ] **Step 3:** Gate G. **Step 4: Commit** — `refactor: 3-step onboarding, fixed 4-item tray, breathe on home, overlay bubble removed, no global hotkey`

---

### Task 12: Remove outfits/seasonal; slim Preferences; self-test; backup UI & safe mode

**Files:**
- Delete: `src/Dudu.Core/Assets/SeasonalOutfitPolicy.cs`; outfit/seasonal types in `src/Dudu.Core/Models/Outfit.cs` (`MonthDay`, `SeasonalDates`) once unreferenced
- Modify: `src/Dudu.Core/Assets/AssetManifest.cs` (`AssetPack.ResolveOutfit`/`ResolveAnimation` → always `base`), `src/Dudu.App/Animation/AnimationEngine.cs` (seasonal context), composition (`RuntimeOutfitKey`, `AvailableOutfitKeys` ~lines 491-494, 624, 1111-1113), `SettingsViewModel`/`SettingsPage.xaml` (outfit + seasonal UI)
- Modify: `src/Dudu.App/Windows/SettingsWindow.xaml.cs` (seasonal/outfit uses)
- Modify: `src/Dudu.Core/Models/Preferences.cs` — remove `QuietHours`, `LocalNoteDailyLimit`, `HydrationRemindersEnabled`, `BreakRemindersEnabled`, `EveningCheckInEnabled`, `BedtimeRitualEnabled`, `OutfitKey`, `AutomaticSeasonalMode`, `Anniversary`, `Birthday`, `GlobalShortcut`
- Modify: `src/Dudu.Infrastructure/Data/Repositories/PreferencesRepository.cs` — INSERT writes fixed legacy values for NOT NULL no-default columns; UPDATE touches only kept columns; SELECT reads only kept columns
- Modify: `src/Dudu.App/Hosting/SelfTestRunner.cs` — `ResolveCoreServices` resolves only surviving services (`ICompanionFeatureTransactions` reduced, `IAppUnitOfWork` reduced); remove checks for deleted repositories
- Modify: composition safe-mode context (~lines 1360-1483) — remove backup/restore commands; safe-mode Settings shows only Delete my data + exit. `DatabaseBackupService`, `MigrationRunner` backup, `Database` corruption restore, `backup-prune` untouched
- Delete tests: outfit parts of `tests/Dudu.Core.Tests/Companion/CompanionFeatureTests.cs`; update `AppearancePageUxTests.cs` (rename to `SettingsPageUxTests.cs`), **both** `tests/Dudu.App.Tests/Hosting/SelfTestRunnerTests.cs` and `tests/Dudu.Infrastructure.Tests/Hosting/SelfTestRunnerTests.cs`, every positional `new Preferences(` call site (~50, incl. 32 in `AppLifecycleCoordinatorTests.cs`; find with `grep -rn "new Preferences(" src tests tools`), `tests/Dudu.Infrastructure.Tests/Data/DatabaseTests.cs`, `SchemaUpgradeTests.cs`, `AssetManifestContractTests.cs`, `AnimationEngine` tests
- Test (new): preferences upsert tests; self-test test

- [ ] **Step 1: Failing tests** (`tests/Dudu.Infrastructure.Tests/Data/PreferencesLegacyColumnsTests.cs`, using the same fresh-DB and upgraded-DB helpers as `SchemaUpgradeTests.cs`):

```csharp
[Fact]
public async Task Fresh_database_saves_and_reloads_preferences()
{
    await using var db = await TestDatabase.CreateAsync();
    var prefs = Preferences.Default with { SoundVolume = 0.5, ReducedMotion = true };

    await db.Preferences.SaveAsync(prefs, TestContext.Current.CancellationToken);
    var loaded = await db.Preferences.LoadAsync(TestContext.Current.CancellationToken);

    Assert.Equal(prefs, loaded);
}

[Fact]
public async Task Upgraded_database_keeps_legacy_columns_untouched_on_update()
{
    await using var db = await TestDatabase.CreateFromMigrationAsync(13);   // existing helper pattern
    await db.ExecuteAsync("UPDATE preferences SET quiet_hours_enabled = 1, local_note_daily_limit = 5 WHERE id = 1");

    await db.Preferences.SaveAsync(Preferences.Default with { ReducedMotion = true }, TestContext.Current.CancellationToken);

    Assert.Equal(1L, await db.ScalarAsync<long>("SELECT quiet_hours_enabled FROM preferences WHERE id = 1"));
    Assert.Equal(5L, await db.ScalarAsync<long>("SELECT local_note_daily_limit FROM preferences WHERE id = 1"));
}
```

(The sketch above uses placeholder helpers: there is no `TestDatabase`/`CreateFromMigrationAsync`/`ScalarAsync`; the repository read method is `GetAsync`, not `LoadAsync`. Copy the `DatabaseFixture` pattern from `DatabaseTests.cs` and the upgraded-DB setup from `SchemaUpgradeTests.cs`, and read columns with a raw `SqliteCommand`.) Update both `SelfTestRunnerTests.cs` files so the production-shaped service provider (built from `DependencyInjection` as today) passes self-test.
- [ ] **Step 2:** Implement per file list.
- [ ] **Step 3:** Gate G. **Step 4: Commit** — `refactor: drop outfits and cut preferences; reduced self-test; no user backup/restore`

---

### Task 13: CompanionFeatureContext & composition sweep

**Files:**
- Modify: `src/Dudu.App/ViewModels/CompanionFeatureContext.cs`, `src/Dudu.App/Hosting/WindowsCompanionProductionComposition.cs`, `src/Dudu.Infrastructure/DependencyInjection.cs`
- Test: `tests/Dudu.App.Tests/Overlay/OverlayTestFeatureContext.cs`, `tests/Dudu.App.Tests/ViewModels/SettingsDataPagesFixture.cs`

- [ ] **Step 1:** Remove any now-unused members, constructor parameters, and registrations left by Tasks 2-12 (find them by `grep` for every removed type/member name from Tasks 2-12 and by reading each constructor parameter's uses — unused parameters do not warn, and with warnings-as-errors an unused private field already fails the build).
- [ ] **Step 2:** Confirm the kept surface: pet/affection/pause/present callbacks, reduced note repo, `ICompanionFeatureTransactions` (remote save+consume only), `DiscardHeldRemoteNoteAsync`, `DiscardHeldRemoteNotesAsync`, pairing, remote delete, local delete, preferences, profile, placement, partner clock.
- [ ] **Step 3:** Gate G. **Step 4: Commit** — `refactor: trim companion feature context to the cute surface`

---

### Task 14: UI tests, harness, docs

**Files:**
- Modify: `tests/Dudu.UiTests/DuduUiFixture.cs` (3-page contract, 3 onboarding steps, remove `PrivacyBackup`/`PrivacyRestore`), `SettingsNavigationTests.cs`, `FullJourneyTests.cs`, `OnboardingTests.cs`
- Modify: `tests/Dudu.WindowsHarness/*` (verify no reminder/focus remnants)
- Modify: `AGENTS.md` Diagnostics section — remove `hotkey-attach`, `hotkey-set-gesture`, `hotkey-restore`, `hotkey` native callback, `reminder-notify`, `reminder-notify-profile`, `reminder-toast-action`, `reminder-page-action`, `focus-restore`; rename `reminder-scheduler` → `presentation-scheduler`; keep `backup-prune`; add: "held rows of retired kinds `Reminder`/`LocalNote` are deleted silently on load"; update the "A reminder that was held…" symptom bullet → remove
- Modify: `PRODUCT.md` (cute-first scope), `docs/testing/windows-acceptance.md` (remove rows for cut features, but keep the "Store visibility" and "second Store update" rows that `release-contract.tests.ps1:88-89` requires; add rows: 3-page nav, reveal keeps note, delete-my-data offline → "wipe this pc only", legacy reminder toast click does nothing, click Dudu still pets, tray pause/resume toggle, breathe + Stop on Home)

- [ ] **Step 1:** Update UI tests to the new contract (they only compile here; they run on Windows CI when `DUDU_ENABLE_UI_AUTOMATION` is on).
- [ ] **Step 2:** Docs edits.
- [ ] **Step 3:** Gate G, plus relay sanity (unchanged, should stay green):

```bash
cd "$DUDU_REPO/relay" && npm ci && npm run typecheck && DUDU_DOTNET="$DUDU_DOTNET" npm test -- --run
```

- [ ] **Step 4: Commit** — `docs,test: align ui tests, harness and docs with the cute-focus scope`

---

### Task 15: Windows CI verification

`windows-installer.yml` triggers on push to `main` or `workflow_dispatch` (input `store_version`). Do **not** push to main.

- [ ] **Step 1:** Push the branch and dispatch CI on it:

```bash
git push -u origin feat/cute-focus
gh workflow run windows-installer.yml --ref feat/cute-focus -f store_version=1.0.0
gh run list --workflow windows-installer.yml --branch feat/cute-focus --event workflow_dispatch --limit 5 \
  --json databaseId,headSha,status,conclusion,url   # pick the run whose headSha == $(git rev-parse HEAD)
gh run watch <run-id> --interval 10 --exit-status
```

- [ ] **Step 2:** If the `tests` job fails, read the failing test output (`gh run view <run-id> --log-failed`), fix, re-run Gate G, commit, push, re-dispatch. XAML compile errors surface only here — expect at least one round.
- [ ] **Step 3:** Green run required on every job in the workflow (currently `tests`, `installer` (includes `--self-test` smoke), `store-package`, `verify-artifact`; read the workflow for the current list). The installer/store jobs upload artifacts that count against the account's Actions storage quota (see `AGENTS.md`); if an upload fails only for quota, report that to the owner rather than retrying repeatedly. Do not download or distribute any artifact.
- [ ] **Step 4:** Report to the owner: run URL, per-suite pass counts, and a short list of anything intentionally deferred. Do not merge to `main` and do not start a Store release; both need explicit owner approval.

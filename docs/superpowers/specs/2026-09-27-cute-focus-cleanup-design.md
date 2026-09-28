# Cute-focus cleanup — design

Date: 2026-09-27
Branch: `feat/cute-focus` (from `main` @ `bcf382b`)

## Intent

The owner wants Dudu to be a cute companion first. The Settings window and
feature set have grown into a productivity app (reminders, tasks, focus
timer, countdowns, mood check-ins, backup) that clutters the interface and
dilutes the point. Success = a small, cute app: Dudu on the desktop, a few
affection interactions, partner love notes, the UK partner clock, and only
the settings needed to run it.

Owner decisions (2026-09-27):

- **Keep:** remote love notes (relay/pairing/sender), UK partner clock.
- **Cut:** local note jar, backup/restore *page*, and every purely practical
  feature listed below.
- **Accepted trade-off:** quiet hours go away; the tray pause is the only
  way to silence Dudu.
- **Approach:** hard delete (remove code end-to-end), not UI hiding and not
  feature flags. UI-only hiding is rejected because existing installs have
  default hydration/break reminders enabled in their databases that would
  keep firing with no UI to stop them.

## Resulting product

### Settings window — 3 destinations (was 7)

| Tag | Title | Contents |
|---|---|---|
| `home` | Home | Big Dudu frame, greeting using the recipient name, UK partner clock card, cute buttons: pet, drink, eat together, tiny hug, breathe with me. Nothing else. |
| `notes` | Love Notes | Pending partner notes (count + list + reveal) and an "opened notes" list to re-read or delete past partner notes. |
| `settings` | Settings | Theme, reduced motion, sounds on/off + volume, pet size, monitor, keep on top, hide during fullscreen, launch at sign-in (moved from Home together with its `StartupSettingsService` wiring and retry panel). A "Partner connection" section hosting the existing `ConnectionViewModel` UI. One "Delete my data" button with confirm. |

Removed destinations: `reminders`, `tasks`, `appearance` (becomes
`settings`), `connection` (folded into `settings`), `privacy`.

Every hard-coded destination tag must be retargeted: `SettingsWindow.xaml`
nav items and `SettingsWindow.xaml.cs` page map, `XamlCompileStubs.cs`,
`OverlayCommandRouter` (DrinkWater's `EquivalentSettingsDestination`
currently returns `"reminders"` — map it to `"home"`, like Pet and
EatTogether), `NotificationActivation.Destination`, and the UI-test
fixtures.

### Love notes flow

- **Reveal = read + keep.** Revealing a pending note calls the existing
  `SaveRemoteNoteAndConsumeEnvelopeAsync` (stores it as `remote-<messageId>`
  in `local_notes` and consumes the envelope in one unit of work), discards
  any held copy, dismisses the pet's unread indicator, then plays the
  sender's reaction one-shot as today. There is no separate "save to jar"
  step; the old `SaveOpenedNoteAsync` logic moves into reveal.
- **Opened notes list** = `local_notes` rows whose id starts with `remote-`
  (existing installs' previously saved partner notes appear here). Delete
  removes a row. Non-`remote-` rows (seeded defaults, user-written notes)
  stay dormant and invisible.
- `ILocalNoteRepository` survives in reduced form for this purpose
  (list by `remote-` prefix, upsert, delete). `LocalNoteSelector`, daily-limit
  logic, the local draft editor and "let Dudu choose" are removed.
- Forget-pairing and single-note discard keep using
  `DiscardHeldRemoteNoteAsync` / `DiscardHeldRemoteNotesAsync`.
- **Order:** `local_notes` has no timestamp column (migration 0009), so the
  opened list is ordered `ORDER BY rowid DESC` (most recently first saved
  first; an `ON CONFLICT DO UPDATE` upsert keeps the row's original rowid and
  position — acceptable).
- **Privacy change (accepted, documented):** today plaintext of a revealed
  note stays in memory unless she explicitly saves it. After this change
  every revealed note is persisted in `local_notes` (and therefore in the
  automatic pre-migration database backups) until she deletes it from the
  opened list or uses Delete my data. Update the "what is stored" copy on the
  Settings page and `docs/release.md`/`PRODUCT.md` privacy wording to say so.

### Delete my data

There is no reliable "paired" flag (`PairingAvailability` is only
Offline/Available/NeedsRepair; the UI's `IsPaired` also requires a sender
session), and remote delete already treats NotFound/Unauthorized as done
(`RemoteSyncService`). The relay never expires the device registration or
sender sessions on its own, and the local wipe destroys the device secrets,
so a local-only wipe leaves the partner able to send into a device nobody
can read. Therefore:

0. **Stop the sync loop first** (`RemoteSyncService` stop, as the local wipe
   does today) so no poll can run `EnsureRegisteredAsync` and re-register a
   fresh device between the remote delete (which clears the local device
   id) and the local wipe.
1. **Remote first, when a relay is configured** (production composition with
   `RemoteSyncService`, i.e. not the offline build and not safe mode): always
   attempt the existing remote device delete, regardless of session count.
   - Success (incl. NotFound/Unauthorized) → continue to step 2.
   - Failure / not-completed (offline, 5xx) → **restart the sync loop, stop,
     wipe nothing**, show
     "couldn't reach the partner server, so your partner can still send
     here. try again when you're online, or wipe this pc only" with a
     second button **"wipe this pc only"** that runs step 2 alone.
2. **Local:** existing `LocalDataMaintenanceService` wipe.
3. Success message keeps today's advice: "all cleaned up -- restart dudu to
   start fresh" (the running app still holds the old profile/preferences in
   memory).

Offline builds and safe mode skip step 1 without calling it a failure. Safe
mode's delete uses exactly the local-delete path safe mode wires today
(unchanged, including its behavior when the database could not be opened).

### Onboarding — 3 steps (was 6)

Name → Look (theme, reduced motion) → Pairing (check or skip).
`CompleteAsync` still saves the profile, preferences and the default pet
placement; `LaunchAtSignIn` and `HidePetDuringFullscreen` default to `true`
(the current onboarding defaults) and are editable later in Settings.
Remove all `LocalReminderDefaults` writes.

### Tray menu

Four fixed items: Show/Hide · Pause 1 hour ⇄ Resume · Open Dudu · Exit.

The native menu maps clicks back through a fixed `Commands` list by index
(`TrayIconService` item id = index + 1, resolved on `WM_COMMAND`), so the menu
must not change length with state. `TrayCommand` becomes `ShowOrHide`,
`PauseOneHourOrResume`, `OpenSettings`, `Exit`; the second item's label
reads "Pause 1 hour" when not paused and "Resume" when paused (same pattern
as today's `PauseIndefinitelyOrResume`). Selecting it while paused (any mode,
including legacy persisted ones) resumes; otherwise applies
`PausePolicy.ForOneHour`. `PauseMode` enum members and `PausePersistence.To/FromPreference`
stay unchanged so every legacy persisted mode restores exactly as today
(including `UntilFullscreenEnds` → None). Delete `PausePolicy` factories that
lose all callers (`ForFiveMinutes`, `UntilTomorrowAtSeven`) only if nothing
else references them.

### Safe mode

Safe mode's settings context loses backup/restore commands. It shows the
Settings page with only the "Delete my data" action (start fresh) available,
plus exit. Automatic corruption recovery (below) is unchanged.

### Pet overlay

- Click pets Dudu (unchanged on `main`).
- Ambient stickers play on their own, with no note-text bubble.
- **Ambient cadence:** today unsolicited ambient is effectively capped by
  `LocalNoteDailyLimit` (default 3/day). Preserve that: an in-memory cap of
  3 unsolicited ambient moments per local calendar day inside
  `PresentationCoordinator` (resets on day change or restart), on top of the
  existing `AmbientScheduler` interval. Only moments that actually played
  count toward the cap.
- **Ambient pool:** `AmbientScheduler`'s pool is idle, blink, greeting,
  sleep, drink, celebrate, sticker-*. Without the note bubble an `idle` pick
  is an invisible 6 s moment, so remove `idle` from the ambient pool; the
  rest stay. `AmbientScheduler` also falls back to `idle` when a pack has no
  stickers (`AmbientScheduler.cs:116-125`); that fallback returns null
  instead (no moment, not counted toward the cap).
- **Ambient audio:** add `AudioCueSelection.ForAmbient(string animationKey)`
  reproducing today's mapping from the `LocalNote` arm of `ForNotification`
  exactly — `sticker-*` → `Sticker`, every other key → `ManualInteraction` —
  at **Background** priority. The direct ambient path uses it (not
  `ForPresentation`, which would map `drink` to an Interactive cue and
  `blink`/`sleep` to silence).
- Welcome-back, tantrum/affection, drag, wander, eat together, tiny hug:
  behavior unchanged.
- **Breathe with me** moves fully onto Home (see §4 Comfort).
- Asset manifest contract unchanged (`focus` stays a required key even
  though nothing plays it; no new fidget keys).

### Removed features

Reminders (custom, helpful defaults, reminder toasts, Done/Snooze toast
actions, reminder bubble/pet state), tasks, focus/pomodoro timer,
countdowns, mood check-ins, local note jar editor/selector and ambient note
text, user-facing backup/restore, global hotkey, quiet hours,
outfit/seasonal picker, the comfort *menu* items "5-minute break" and "read
a love note".

## Architecture changes

### 1. Presentation heartbeat survives reminders

`AppHost`'s reminder scheduler (30 s timer) also drives
`TickPresentationGatewayAsync` (ambient, tantrum, held-note release) and,
through `ReminderEngine.TickAsync`, focus expiry.

- Replace `RunReminderTickAsync` with `RunPresentationTickAsync`:
  `ReconcileVisibilityAsync` → `TickPresentationGatewayAsync`, serialized by
  the existing gate (renamed).
- Startup call site (currently a reminder-only tick with
  `releasePresentations: false`, and startup deliberately does **not**
  reconcile visibility): startup now only starts the presentation gateway;
  the first 30 s timer tick does reconcile + release, as today.
- Resume/unlock catch-up: reconcile + gateway tick.
- Remove `IAppHostReminderService`, `ReminderHostService`, the
  reminder-engine constructor overloads, and rename the `reminder-scheduler`
  error-report operation to `presentation-scheduler` (update AGENTS.md).

### 2. PresentationCoordinator, PresentationPolicy, DurableNotification

- `PresentationItemKind`: remove `Reminder` and `LocalNote`. Keep
  `RemoteNote` with its current name (persisted rows store the name).
  Remove `Ambient` and its test-only factory in `PresentationPolicy`.
- Delete Reminder/LocalNote branches in `PresentAsync`,
  `ShowNotificationAsync`, `ToPetEvent`, `ToDurableNotification`, and
  `DurableNotification.Reminder/LocalNote` factories.
- **Keep** `DiscardHeldAsync` and `DiscardHeldByKindAsync` (remote notes use
  them for forget-pairing and single-note discard); drop only
  Reminder/LocalNote uses.
- Remove the `LocalNoteSelector` dependency and the
  ambient-scheduler/selector pairing invariant.
- **Ambient:** present directly, like `PresentTantrumAsync` — best-effort,
  not durable, skipped when suppressed, subject to the daily cap above,
  event `PetEvent.AmbientRequested(animationKey)`, audio via
  `AudioCueSelection.ForAmbient`. After an ambient moment plays, call
  `_policy.RecordImmediateRelease` exactly as today's ambient-note path does,
  so a held remote note still waits the minimum silent interval after it.
- `PresentationPolicy` (`src/Dudu.App/Presentation/PresentationPolicy.cs`):
  remove `IsRoutine`/`IgnoresQuietHours` (they reference
  `LocalReminderDefaults`), drop the `nowQuiet` and `focusActive`
  parameters from `Decide`, replace with `busy`.
- **Suppression:** `SuppressionSnapshot` drops `NowQuiet` and replaces
  `FocusActive` with `Busy = _pet.IsEatingActive || _pet.IsDragging`
  (today `focusActive = IsFocusActive || IsEatingActive || IsDragging`).
  `PetStateMachine.IsQuietCompanyActive` becomes eating-only and still holds
  the remote-note card during a meal.
- **Legacy held rows:** load failures already delete the row but emit a
  throttled `presentation-held-load` report. Known legacy kinds
  (`Reminder`, `LocalNote`) must be deleted silently (no diagnostic);
  genuinely unknown kinds keep today's delete-and-report behavior.

### 3. Pet state machine (Core)

- Remove `PetState.Reminder`, `PetState.Focus`, `PetState.FocusTransition`,
  events `ReminderDue`, `FocusStarted`, `FocusEnded`, and `IsFocusActive`.
  Keep `Eating`, `Interaction`, `Dragging`, `Comfort`, `WelcomeBack`,
  `Ambient`, `RemoteNote`, `Idle`.
- Remove the quiet-hours input from `AmbientScheduler`, `PetActivityGate`,
  `AudioCueService` suppression, and the welcome-back decision in
  `AppLifecycleCoordinator` (optional delegate; safe to drop).
- `AudioCueSelection`: drop the `Reminder` cue event only.

### 4. App shell

- `SettingsShellViewModel` destinations → `home`, `notes`, `settings`
  (automation IDs `NavHome`, `NavLoveNotes`, `NavSettings`).
- Delete pages `RemindersPage`, `TasksFocusPage`, `PrivacyDataPage`,
  `ConnectionPage`. Rename `AppearancePage` → `SettingsPage` and
  `AppearanceViewModel` → `SettingsViewModel`, which composes the existing
  `ConnectionViewModel` and a delete-data confirm.
- Delete viewmodels `RemindersViewModel`, `TasksFocusViewModel`,
  `PrivacyDataViewModel`. Slim `HomeViewModel` (drop status, next reminder,
  focus, countdowns, check-in, startup, dudu-actions navigation),
  `LoveNotesViewModel` (per "Love notes flow"), `OnboardingViewModel`
  (3 steps).
- `SettingsWindow` header: keep Dudu frame + pet/drink/hug buttons.
- `CompanionFeatureContext`: remove reminder/task/focus/countdown/check-in
  services and repos, `LocalNoteSelector`, backup/restore, outfit, shortcut,
  `UpdatePreferencesAndDefaultRemindersAsync`, and reminder discard
  callbacks. Keep the reduced note repo, feature transactions (reduced),
  remote-note discard callbacks, pairing, remote delete, local delete.
- **Overlay action surface: remove the bubble, keep click-to-pet.** Since
  59070b2 ("clicking Dudu pets it; nothing pops up")
  `OverlayActionSurfaceController.Open` has no production caller, so the
  bubble/comfort panel can never appear. But a body click still goes
  `OverlayWindowHost` → `OverlayActionDispatchQueue.EnqueuePet(surface)` →
  `OverlayActionSurfaceController.PetFromBodyAsync` → `router.ExecuteAsync(Pet)`,
  and that must keep working. Therefore:
  - Delete `Open`, the bubble/comfort layout (`ActionBubbleLayout` geometry),
    the action/comfort drawing in `OverlaySurfaceRenderer`/`SkiaFrameComposer`,
    and the surface's hit-testing/pointer/viewport branches in
    `OverlayWindowHost`, plus their tests.
  - Keep the pet path: the dispatch queue calls
    `router.ExecuteAsync(OverlayAction.Pet)` directly (the controller is
    deleted), preserving the queue's serialization and error reporting.
    Today the host receives the controller via
    `OverlayWindowHost.SetActionSurfaceAsync` and the router is bound later
    (`actionSurface.Bind(overlayRouter)`); replace that with
    `SetPetHandlerAsync(Func<CancellationToken, Task>)` wired to
    `ct => overlayRouter.ExecuteAsync(OverlayAction.Pet, ct)` once the router
    exists (the overlay-start callback runs after composition returns), and
    `EnqueuePet` takes the handler instead of the controller.
  - Drop `|| actionSurface.IsOpen` from the idle-activity busy check in
    `WindowsCompanionProductionComposition`.
  - Move `ActionBubbleLayout.Label`/`ComfortLabel` strings (used by
    `HomePage.xaml.cs` status text and `OverlayCommandRouter`) into
    `OverlayCommandRouter` as static label helpers.
  - Keep `OverlayCommandRouter` as the command sink for overlay click and
    Home/header buttons.
- **Comfort:** `OverlayAction` → `Pet`, `DrinkWater`, `EatTogether`,
  `TinyHug`, `BreatheWithMe`. Remove `StartFocus`, `Tasks`, `LoveNote`,
  `ComfortMe`. `ComfortAction` → `BreatheWithMe`, `Close`.
  `ComfortPanelState` stays as the breathing state model.
- **Breathe with me on Home:** the Home button already runs the 60 s
  breathing loop, invisibly. Home subscribes to `ComfortPanelChanged` and
  shows the breathing instruction + phase text while it runs, with a **Stop**
  button that sends `ComfortAction.Close`. Leaving the page or pressing Stop
  cancels it. Cancellation (`TaskCanceledException`/`OperationCanceledException`
  out of `BreatheWithMeAsync`) is the normal "stopped" outcome: the Home click
  handler shows no error for it.
- Delete `GlobalHotkeyService`, its bootstrap wiring, and persisted-shortcut
  restore.
- Delete `ReminderToastActions` and `ReminderDueSink`.
  `NotificationActivation`: delete `ReminderDone`, `ReminderSnooze`,
  `OpenReminder`, the `ReminderId` field and `ActsInBackground`;
  `TryParse`/`Resolve` return **null** for `reminder-*` actions exactly as
  they already do for unknown actions (existing contract and
  `NotificationActivationTests` unknown-action test). `Destination` then only
  ever returns `"notes"`. `NotificationInvocationRouter` and the cold-start
  path in `App.xaml.cs` already treat a null activation as "just open/ignore",
  so an old reminder toast click neither acts nor targets a removed page.
- **Outfits:** remove the outfit picker and seasonal UI from Settings,
  `RuntimeOutfitKey`/`AvailableOutfitKeys` plumbing in the composition, the
  AnimationEngine seasonal context, `SeasonalOutfitPolicy`, and
  `AssetPack.ResolveOutfit` seasonal logic — animation resolution always
  uses the `base` outfit. `MonthDay`/`SeasonalDates` are removed once
  `Preferences` no longer references them.

### 5. Core / Infrastructure

**Delete:**

- Core: `Reminders/`, `Tasks/`, `Focus/`, `Countdowns/`, `CheckIns/`,
  `Notes/LocalNoteSelector.cs`, `Policies/QuietHoursPolicy.cs`,
  `Assets/SeasonalOutfitPolicy.cs`, their abstractions
  (`IReminderRepository`, `IReminderDueSink`, task/focus/countdown/check-in
  repositories) and models (`Reminder`, `RecurrenceRule`, `TaskItem`,
  `FocusSession`, `MoodCheckIn`, `Countdown`, outfit/seasonal types).
- Infra: matching repositories and `IAppUnitOfWork` members;
  `CompanionFeatureTransactionService` reminder/preference methods
  (`Save/RestorePreferencesAndDefaultRemindersAsync`) — keep
  `SaveRemoteNoteAndConsumeEnvelopeAsync`; DI registrations.
- `SeedData` default-note seeding: `Database` initialization and
  `LocalDataMaintenanceService` stop seeding notes; `seed_state` is left
  dormant (not written, still wiped). Existing seeded rows stay dormant.

**Keep (explicitly):**

- `DatabaseBackupService` and all automatic behavior: pre-migration backup
  (`MigrationRunner`), corruption auto-restore and interrupted-restore
  reconcile (`Database`), `DatabaseRecoveryOutcome` notice, backup pruning
  and the `backup-prune` diagnostic phase. Only the user-facing Backup /
  Restore commands and safe-mode backup/restore callbacks are removed.
- `LocalDataMaintenanceService` — still deletes every dormant table so a
  privacy wipe stays complete.
- `ILocalNoteRepository` (reduced), `IRemoteEnvelopeRepository`, pairing,
  relay, crypto, DPAPI secrets.

**Preferences:** model drops `QuietHours`, `LocalNoteDailyLimit`,
hydration/break/evening/bedtime flags, outfit/seasonal/anniversary/birthday,
`GlobalShortcut`. Keeps theme, reduced motion, launch at sign-in, always on
top, hide during fullscreen, `AmbientMinimumInterval`, sounds, volume, pause
mode/expiry.

**Schema:** no migrations. Migration 0001 columns `quiet_hours_enabled`,
`quiet_hours_start`, `quiet_hours_end`, `local_note_daily_limit` are
`NOT NULL` without defaults, so the preferences upsert keeps writing fixed
values for them on INSERT (`0`, `'22:00'`, `'07:00'`, `0`) and does not touch
them on UPDATE. Later columns have defaults or are nullable.

**SelfTestRunner:** `ResolveCoreServices` resolves
`ICompanionFeatureTransactions` and `IAppUnitOfWork`; update its resolution
and checks to the reduced surface. `tests/installer/installer-smoke.ps1`
runs `--self-test`, so this must stay green.

### 6. Tests, harness, docs

- Delete tests of removed features (never weaken remaining assertions).
- `tests/Dudu.WindowsHarness`: remove reminder scenarios in `Program.cs` and
  the reminder-occurrence assertions in `LongRunScenario.cs`; keep its
  database/backup options.
- `tests/Dudu.UiTests`: update `DuduUiFixture` (7-page contract, onboarding
  steps), `SettingsNavigationTests` (3 pages; drop `PrivacyBackup` /
  `PrivacyRestore`), `FullJourneyTests`, `OnboardingTests`.
- Docs: `AGENTS.md` diagnostics (remove `hotkey-*`, `reminder-*`,
  `reminder-toast-action`, `reminder-page-action`, `focus-restore`; rename
  `reminder-scheduler`; keep `backup-prune`), `PRODUCT.md`,
  `docs/testing/windows-acceptance.md`.

## Out of scope

- Table/column-dropping migrations.
- Relay worker and sender page.
- New art or sounds; manifest contract changes.
- Store release (separate, explicit request).

## Required new regression tests

1. Ambient presents with no note store and no bubble text; daily cap of 3
   counts only played moments and resets on day change; `idle` is no longer
   picked; ambient audio uses `ForAmbient` at Background priority; a held
   remote note still waits the silent interval after an ambient moment
   (`PresentationCoordinatorTests`, `AudioCueSelection` tests).
2. Legacy `Reminder`/`LocalNote` held rows are deleted on load without a
   diagnostic report; `RemoteNote` rows alongside them still load.
3. `AppHost` ticks the presentation gateway on its timer with no reminder
   engine; startup does not reconcile or release.
4. `NotificationActivation.TryParse` returns null for `reminder-done`,
   `reminder-snooze`, `open-reminder`; invoking the router / cold-start
   handler with those arguments performs no action and throws nothing.
5. Eating and dragging both suppress ambient/tantrum; eating holds the
   remote-note card.
6. `PausePersistence.FromPreference` restores every `PauseMode` exactly as
   the unmodified code does today (pin current outcomes first).
7. Preferences upsert on a fresh and on an upgraded database succeeds and
   leaves the legacy NOT NULL columns untouched on update.
8. `SettingsShellViewModel` exposes exactly `home`, `notes`, `settings`.
9. Revealing a remote note consumes the envelope and adds it to opened
   notes (newest first); opened list shows only `remote-` rows.
10. Delete-my-data: sync loop is stopped before the remote call; remote
    success → local wiped; remote failure → nothing wiped, sync loop
    restarted, "wipe this pc only" offered and wipes local only; offline
    build / safe mode → local only, no failure message.
11. Self-test passes with the reduced service graph.
12. Tray: the menu has 4 items in both states and each click maps to the
    labelled command (paused and unpaused).
13. Home breathe: starting shows phase text; Stop sends `Close`, hides it,
    and shows no error.
14. Clicking Dudu's body still routes `OverlayAction.Pet` through the
    dispatch queue and plays the petted reaction; the idle-activity busy
    check no longer references the action surface.
15. `AmbientScheduler` with a pack that has no stickers returns null rather
    than `idle`.

## Verification gates

- Non-Windows: Core and Infrastructure tests run; App, UiTests,
  WindowsHarness compile in stub mode (flags in `AGENTS.md`); relay tests
  still run.
- Windows CI (`windows-installer.yml`, push to `main` or manual dispatch) is
  authoritative for XAML, App tests, UiTests and the installer self-test.
  XAML rewrites are the highest-risk part: they cannot be compiled for real
  off Windows.

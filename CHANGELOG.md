# Changelog

All notable changes to Dudu Desktop Companion are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
this project is private-use and does not follow public semantic-versioning
release cadences.

## [Unreleased]

### Fixed

- Harden first-start reliability after install: the global shortcut and
  tray icon are now best-effort (an already-owned hotkey or an unavailable
  notification area no longer exits the app), toast activation subscribes
  best-effort when the Windows App Runtime registration is broken, a
  corrupt local database is quarantined to `backups/dudu-corrupt-*.db`
  and recreated instead of crash-looping every launch (including safe
  mode), and launches on older Windows fail with a clear `os-version`
  startup phase — the installer now requires Windows 11 24H2
  (build 26100) up front.
- Merge the WinUI control resources (`XamlControlsResources`) before the
  app's custom dictionaries so standard controls resolve their default
  styles on first launch and in safe mode.

### Fixed (audit batch 2)

- Local data: a corrupt database is quarantined and recreated instead of
  crash-looping; wipe-and-reseed is a single transaction; restores that
  fail outside IO/SQLite still drop the cached initialization; reminder
  compare-and-set rollback no longer observes caller cancellation;
  envelope pruning compares deliver-after instants (relay timestamp
  strings stay byte-for-byte verbatim because they are bound into the
  encryption AAD); default notes are seeded once (deleted defaults stay
  deleted; full wipe still restores them); concurrent focus starts yield
  exactly one active session.
- App behavior: wiping local data works with no relay configured; routine
  reminders with an unknown time zone fall back to UTC instead of being
  silently lost; the settings pet timer freezes on its last frame instead
  of crashing the app; null selections report "select one first"; saving
  a note/reminder clears the editor (plus explicit New buttons) so fresh
  input cannot overwrite the just-saved item; date hints follow the
  device locale; Feb-29 anniversaries/birthdays celebrate on Feb-28 in
  non-leap years; empty relay pairing codes are rejected loudly.
- Release tooling: the WACK Store-readiness check no longer rejects valid
  WARNING reports (the XML adapter already stringifies `RESULT`, so
  `.InnerText` was always null).

## [1.0.0-private.1] - 2026-09-13

### Added

- Documented the private Microsoft Store submission/update path, recipient
  installation procedure, and Inno fallback during the package-identity
  migration. Store distribution remains pending real Windows acceptance.
- First-launch onboarding that names Dudu, sets appearance and quiet hours,
  offers hydration/break reminders, places the pet on the desktop, and
  optionally enables launch-at-sign-in and sender pairing, in under two
  minutes with defaults accepted.
- Everyday desktop pet interaction: click to open a context-sensitive action
  bubble, drag to move, scroll to resize, double-click for Home settings,
  right-click for a compact pause/appearance/settings/exit menu, a global
  show/hide shortcut, and automatic bubble suppression during fullscreen
  activity.
- Reminders with daily, selected-weekday, weekly, and interval recurrence,
  quiet-hours-aware delay, a `Done`/`Snooze`/`Open` bubble, and an optional
  Windows app notification with the Dudu bubble as the reliable fallback.
- Lightweight tasks and focus sessions with preset or custom durations, a
  quiet focus animation, suspended ambient prompts and local notes during
  focus, pause/extend/end controls, and a reduced-motion-aware completion
  celebration.
- A local love-note jar with preloaded and user-editable affectionate
  messages, a daily cap, quiet-hours awareness, and a non-repeating
  selector.
- End-to-end encrypted remote love notes: a paired mobile sender page writes
  a note, previews it locally, encrypts it in the browser, and uploads only
  the ciphertext envelope; the desktop acknowledges delivery, stores the
  still-protected payload until shown, and decrypts on open without any
  network request or read receipt.
- A manually activated bad-day comfort mode offering breathing together, a
  hug animation, reading a love note, a short break, or dismissal, with no
  automatic emotion inference.
- Countdowns for birthdays, anniversaries, trips, or arbitrary events, with
  an occasional anticipatory animation that never becomes a persistent
  desktop obstruction.
- A manual mood check-in with a fixed set of options, an optional private
  note, a suggested (never auto-started) follow-up action, and a local-only
  recent history — never sent to the paired sender or to Cloudflare.
- Outfits and seasonal behavior: a chosen outfit or an `Automatic` mode that
  selects bundled seasonal variants from the local calendar date, using no
  location or remote data, always overridable.
- A private, current-user Windows installer (Inno Setup) with checksum
  verification, in-place upgrade, and clean uninstall without elevation.

### Notes

- This is a private build for a single named recipient. The artwork, relay
  Worker URL, sender page, installer, and this release tag must never be
  published or redistributed — see `docs/release.md`.

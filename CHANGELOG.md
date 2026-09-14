# Changelog

All notable changes to Dudu Desktop Companion are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
this project is private-use and does not follow public semantic-versioning
release cadences.

## [1.0.0-private.1] - 2026-09-13

### Added

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

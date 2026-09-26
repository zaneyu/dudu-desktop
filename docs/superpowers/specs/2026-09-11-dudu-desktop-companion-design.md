# Dudu Desktop Companion — Design Specification

**Date:** 2026-09-11  
**Status:** Approved design, awaiting written-spec review  
**Platform:** Windows 11 x64  
**Distribution:** Private, per-user application  

## 1. Product summary

Dudu Desktop Companion is a private Windows 11 desktop pet made for the user's girlfriend. It places an animated Dudu character directly on the desktop and combines affectionate ambient behavior with practical reminders, lightweight task and focus tools, comfort interactions, countdowns, and private love notes.

The application should feel like a gentle companion rather than a productivity dashboard. Dudu may wave, sleep, celebrate, offer comfort, or surface a reminder, but must remain quiet, dismissible, and respectful of fullscreen activity and configured quiet hours.

A paired mobile-friendly web page lets the user send encrypted notes and simple affectionate reactions to the desktop. Pairing happens once with a short-lived code. The sender then keeps a secure browser session. Cloudflare relays encrypted envelopes but cannot read note contents.

The inspiration is the desktop-pet interaction shown in the supplied TikTok: a transparent, draggable character that lives above the desktop, provides small animated interactions, and can surface personal reminders and check-ins. This project expands that idea into a polished Windows-only private application.

## 2. Goals

The product must:

1. Put a polished, transparent Dudu character on a Windows 11 desktop without behaving like a normal rectangular app window.
2. Offer useful everyday support through reminders, tasks, focus sessions, countdowns, hydration prompts, and quiet hours.
3. Provide affectionate interactions through local prewritten notes and remotely sent encrypted notes.
4. Remain functional offline except for receiving remote notes.
5. Protect private message contents from the relay service and from accidental disclosure in Windows notifications or screen sharing.
6. Feel modern and native to Windows 11 through WinUI 3, Mica, rounded surfaces, restrained motion, and accessible controls.
7. Install without administrator privileges and require little ongoing maintenance.
8. Keep all Dudu art replaceable through a documented asset-pack format.

## 3. Non-goals

Version 1 will not include:

- AI chat, generated advice, or an LLM dependency
- Voice input, speech recording, camera access, or microphone access
- Screen reading, activity surveillance, location tracking, or inferred moods
- Email, calendar, social-network, or contact integrations
- Public accounts, public profiles, friend discovery, or multi-user social features
- Advertising, telemetry, behavioral analytics, or engagement optimization
- Gamified guilt mechanics, punitive streaks, or negative reactions to inactivity
- A public asset marketplace or public redistribution of the collected Dudu artwork
- macOS, Linux, Windows 10, ARM64, or Microsoft Store support in version 1

## 4. Target environment

- Windows 11 version 24H2, build 26100 or newer
- x64 processor architecture
- .NET 10 LTS
- Windows App SDK 2.3.1 and WinUI 3
- A self-contained, unpackaged, per-user deployment
- Installation under `%LOCALAPPDATA%\\Programs\\DuduDesktop`
- Application data under `%LOCALAPPDATA%\\DuduDesktop`
- No administrator rights required
- Modern Safari, Chrome, or Edge for the sender web page
- Cloudflare Workers and D1 for the optional remote-note relay

The desktop application is the primary product. If Cloudflare is unavailable, the pet, reminders, tasks, focus sessions, local love notes, countdowns, and appearance features continue to work.

## 5. Experience principles

### 5.1 Calm by default

Dudu should spend most of the time in subtle idle animations. Unsolicited interruptions are deliberately limited. Local affectionate notes may appear at most three times per day by default. Explicitly scheduled reminders are not included in that cap.

### 5.2 Private by default

Incoming messages initially display only `A note arrived 💌`. Message text appears only after the recipient intentionally opens the note. No read receipt is sent to the sender. The sender may see only relay-level states such as queued, delivered to the desktop, expired, or failed.

### 5.3 Easy to dismiss

Every unsolicited surface can be dismissed immediately. The user can pause Dudu for one hour, until tomorrow, indefinitely, or until fullscreen activity ends. There is no scolding, sad animation, lost streak, or penalty for dismissing or disabling a feature.

### 5.4 Native but characterful

Settings should look like a modern Windows 11 application rather than a themed web page. The pet itself may be playful and illustrated, while controls use standard Windows interaction patterns.

### 5.5 Local-first reliability

The local database is authoritative for reminders, tasks, preferences, focus sessions, countdowns, and local notes. Network connectivity must never be required to launch or use the companion.

## 6. Primary user journeys

### 6.1 First launch

On first launch, a short onboarding flow asks the recipient to:

1. Choose the name Dudu uses for her.
2. Select light, dark, or system appearance.
3. Set quiet hours or accept the recommended default.
4. Enable or skip hydration and break reminders.
5. Place and resize Dudu on the desktop.
6. Optionally enable launch at sign-in.
7. Optionally pair the private sender page.

Pairing is skippable and can be completed later. Onboarding should take less than two minutes if defaults are accepted.

### 6.2 Everyday pet interaction

- A single left click opens a compact action bubble near Dudu.
- Dragging Dudu moves the pet without activating a conventional window.
- The mouse wheel over Dudu adjusts pet size within safe bounds.
- Double-clicking opens the Home settings page.
- Right-clicking opens a compact context menu with pause, appearance, settings, and exit actions.
- A configurable global shortcut shows or hides Dudu.
- The app automatically suppresses unsolicited bubbles during fullscreen activity.

The action bubble contains context-sensitive actions such as `Pet`, `Drink water`, `Start focus`, `Tasks`, `Love note`, and `Comfort me`. It should show no more than six primary actions at once.

### 6.3 Reminder

When a reminder becomes due, Dudu enters the reminder state and shows a compact bubble with the reminder title and appropriate actions: `Done`, `Snooze`, and `Open`. If Windows app notifications are enabled, the app may also send a normal notification. If notification registration fails or permission is unavailable, the Dudu bubble remains the reliable fallback.

Recurring reminders support daily, selected-weekday, weekly, and interval schedules. Quiet hours delay non-urgent reminders until the next allowed time. The reminder editor makes the delay behavior explicit.

### 6.4 Task and focus session

The recipient can create lightweight tasks with a title, optional notes, optional due date, and completed state. A task can start a focus session using a preset or custom duration.

During focus mode:

- Dudu uses a quiet focus animation.
- Ambient prompts and local notes are suspended.
- Remote notes remain queued until the focus session ends or the recipient manually opens the inbox.
- The recipient can pause, extend, or end the session.
- Completion triggers a brief celebration that obeys reduced-motion settings.

The feature is intentionally smaller than a full project-management system. It has no projects, assignees, dependencies, or team sharing.

### 6.5 Local love note

The local love-note jar contains preloaded affectionate messages and user-editable messages. Dudu can surface one opportunistically, subject to the daily cap and quiet hours, or the recipient can request one manually.

The note selector avoids immediate repetition and records only the minimum local history required to rotate messages fairly.

### 6.6 Remote love note

The sender opens the paired mobile page, writes a note, optionally chooses a delivery time, and may attach one reaction: wave, heart, hug, or celebrate. The page shows a plaintext preview locally, encrypts the payload in the browser, and uploads only the encrypted envelope.

When the desktop receives the envelope, it acknowledges device delivery, stores the still-protected payload locally if it cannot display it immediately, and shows `A note arrived 💌` at an appropriate time. The recipient clicks to decrypt and reveal the note. Opening the note does not contact the server and therefore cannot create a read receipt.

### 6.7 Bad-day comfort mode

Choosing `Comfort me` manually activates a calmer state. Dudu presents a small set of optional actions such as breathing together, receiving a hug animation, reading a love note, starting a short break, or dismissing the panel. The app never claims to diagnose an emotion and does not infer this state from computer activity.

### 6.8 Countdown

The recipient can create countdowns for birthdays, anniversaries, trips, or arbitrary events. Dudu may show an occasional anticipatory animation as the event approaches. A countdown never becomes a persistent desktop obstruction.

### 6.9 Manual mood check-in and history

The recipient can optionally record a lightweight check-in using a small fixed set such as `Great`, `Okay`, `Tired`, or `Rough`, with an optional private note. A check-in may suggest an appropriate manual action—celebrate, focus, take a break, or enter comfort mode—but never starts one without confirmation.

Check-ins remain local to the Windows PC. They are not sent to the paired sender or Cloudflare. The Home page may show a simple recent history and counts by selection, but the app does not diagnose, score, predict, or infer mental health.

### 6.10 Outfits and seasonal behavior

The recipient can choose an outfit or use `Automatic`. Outfit variants are supplied by the selected asset pack and fall back to the base character when a required animation is unavailable. Automatic mode may select bundled seasonal variants from the local calendar date, such as birthday, winter, or anniversary themes. It uses no location or remote data, and every automatic choice can be overridden or disabled.

## 7. Visual and interaction design

### 7.1 Settings application

The settings application uses WinUI 3 with:

- A Mica backdrop where supported
- A custom title bar that preserves standard window controls
- `NavigationView` for top-level sections
- Rounded cards with restrained pastel accents
- Segoe UI Variable typography
- System-aware light and dark themes
- High-contrast compatibility
- Keyboard navigation and visible focus indicators

The visual direction is modern, warm, and uncluttered. The default palette uses cream, soft blush, muted lavender, and a limited warm coral accent. Color must not be the sole carrier of status.

Top-level pages are:

1. **Home** — pet status, pause state, next reminder, current focus session, optional mood check-in/history, and quick actions
2. **Reminders** — reminder list, editor, schedules, hydration, and break defaults
3. **Tasks and Focus** — active tasks, completed tasks, focus presets, and focus history
4. **Love Notes** — local note jar, remote-note inbox state, and delivery preferences
5. **Appearance** — pet size, monitor, animation intensity, outfit, theme, and reduced motion
6. **Connection** — pairing code, paired sender sessions, reconnect, and disconnect
7. **Privacy and Data** — data explanation, notification privacy, backup, restore, and delete-data controls

### 7.2 Pet overlay

The pet is rendered in a borderless Win32 layered window with per-pixel alpha. The window is excluded from Alt+Tab and the taskbar. It must not take keyboard focus when clicked or dragged.

The window uses the extended styles required for a layered tool window and applies no-activate behavior. Hit testing is restricted to the visible pet and intentional interaction surfaces rather than the full rectangular bitmap bounds.

The overlay supports:

- Smooth dragging with monitor-edge clamping
- Per-monitor DPI awareness
- User-controlled scale with a safe minimum and maximum
- Remembered placement per monitor configuration
- Repositioning to the primary work area if the saved display no longer exists
- Optional always-on-top behavior, enabled by default
- Fullscreen suppression for unsolicited content

The pet window and WinUI settings window belong to the same desktop application process in version 1, but are separate windowing surfaces. A fatal overlay-rendering failure must not corrupt persisted user data.

### 7.3 Animation language

Version 1 includes these semantic animations:

- Idle
- Blink
- Greeting
- Sleep
- Drink reminder
- Focus
- Celebrate
- Comfort or hug
- Note arrival

Optional motion clips (`walk`, `hop`, `dance`, `wiggle`, `shy`, `sip`, `snack`, `nap`, `stomp`, `shiver`, `lounge`, `salute`) are silent one-shots derived from the private sticker and GIF art. A pack may ship any subset; only clips the loaded pack ships are ever requested.

An outfit may override individual animation frame sets. If an outfit does not provide a requested state, the engine uses that outfit's idle fallback and then the base pack's matching animation. Seasonal variants follow the same fallback chain.

Animations are described by an asset manifest rather than hard-coded filenames. Each animation defines frame sources, frame timing, loop behavior, anchor point, nominal size, and optional reduced-motion fallback.

Default rendering is approximately 15 frames per second, adjusted to the source artwork. The engine may skip frames to preserve responsiveness but must not speed up the semantic duration of one-shot actions. Reduced-motion mode uses static poses, opacity fades, and minimal translation.

## 8. Behavioral state model

The pet is controlled by a deterministic state machine. When several events compete, the highest-priority eligible state wins:

1. Manual comfort mode
2. Unread remote note arrival
3. Due reminder
4. Focus transition or completion
5. Welcome back after unlock or resume
6. Ambient behavior
7. Idle

Quiet hours, global pause, fullscreen suppression, and reduced-motion preferences act as policy gates around this priority list.

### 8.1 Interruption rules

- A manual action may interrupt any ambient or idle animation.
- A reminder does not interrupt an actively opened note or manual comfort sequence.
- A remote note waits while focus mode or fullscreen suppression is active unless the recipient manually opens the inbox.
- A focus-completion celebration waits until a manual comfort action finishes.
- Ambient animations never queue; stale ambient events are discarded.
- Due reminders and remote notes remain durable until handled, expired, or deleted.

### 8.2 Idle behavior

The ambient scheduler selects a behavior from those allowed by time, quiet hours, daily caps, reduced-motion settings, and recent history. It uses bounded randomness so behavior feels varied without becoming unpredictable. It must enforce a minimum silent interval after any unsolicited interaction.

Between those note-backed ambient moments, `PetActivityScheduler` keeps the pet alive with frequent, silent idle activity: every 25–75 seconds it picks either a motion clip to play in place or a short sideways wander (the `walk` clip while `OverlayWindowHost` glides the window up to a few hundred pixels inside the monitor work area, turning around at an edge and persisting where it stops). `PetActivityDirector` plays these only through the shared one-shot path and only while the pet is plainly idle, so they never preempt a higher-priority state. Reduced motion, pause, quiet hours, fullscreen, session lock, a hidden pet, an open action bubble, a drag, or the pointer resting on the pet keep Dudu still, and any suppression holds the next activity at least 25 seconds out.

## 9. Desktop architecture

### 9.1 AppHost

`AppHost` owns application startup, single-instance coordination, service composition, shutdown, sign-in launch registration, tray behavior, and lifecycle events such as session lock, unlock, sleep, resume, and display changes.

Launching a second instance activates the existing instance and opens Home rather than starting a duplicate overlay or scheduler.

### 9.2 OverlayWindowHost

`OverlayWindowHost` owns the Win32 layered window, alpha-aware hit testing, monitor placement, DPI changes, drag gestures, mouse-wheel scaling, and show/hide policy. It accepts already-composited frames from the animation engine and presents them using `UpdateLayeredWindow`.

### 9.3 AnimationEngine

`AnimationEngine` loads asset manifests, validates frames, composes premultiplied BGRA frames, schedules frame advancement, and exposes reduced-motion fallbacks. Rendering should use SkiaSharp into a reusable bitmap buffer to avoid unnecessary allocations.

### 9.4 PetStateMachine

`PetStateMachine` accepts typed events from reminders, focus sessions, note delivery, user actions, lifecycle events, and the ambient scheduler. It produces one authoritative pet presentation state. State selection must be deterministic and independently unit-testable.

### 9.5 ReminderEngine

`ReminderEngine` calculates next occurrences, applies quiet-hour policy, emits due events, handles snoozing and completion, and reconciles missed events after sleep or shutdown. It uses wall-clock time with stored time-zone identifiers and explicitly handles daylight-saving changes.

### 9.6 TaskService and FocusService

`TaskService` owns task CRUD and completion. `FocusService` owns focus timing, pause/resume, completion, and recovery after sleep or application restart. Focus end time is persisted so a timer does not depend on an in-memory tick loop for correctness.

### 9.7 NoteService

`NoteService` owns the local note jar, received encrypted-note queue, reveal behavior, repetition control, expiry, and notification privacy. Remote plaintext exists only in memory while a note is open unless the recipient explicitly saves it to the local jar.

### 9.8 CheckInService

`CheckInService` stores optional manual mood check-ins and produces local-only history summaries. It has no network dependency and exposes no inferred labels or health claims.

### 9.9 RemoteSyncService

`RemoteSyncService` registers the device, creates pairing codes, polls for encrypted envelopes, validates envelopes, deduplicates message IDs, acknowledges delivery, and applies retry backoff. It never blocks local startup or local features.

### 9.10 SettingsWindow

`SettingsWindow` is the WinUI 3 control surface. Its view models call application services rather than directly reading or writing the database. Changes with immediate visual impact update the overlay live; destructive data actions require explicit confirmation.

## 10. Local data design

SQLite is the local system of record. Schema migrations are versioned and transactional. Before an upgrade migration, the application creates a timestamped backup and retains a small bounded number of recent backups.

Logical entities include:

- `Profile` — recipient display name and onboarding state
- `Preferences` — theme, quiet hours, animation, privacy, startup, pause, and notification settings
- `PetPlacement` — monitor identity, normalized position, scale, and always-on-top preference
- `Reminder` — title, details, recurrence definition, next due time, quiet-hour behavior, and active state
- `ReminderOccurrence` — due, snoozed, completed, skipped, or expired occurrence records
- `Task` — title, notes, due time, completion state, and timestamps
- `FocusSession` — task association, planned duration, timing, pause data, and outcome
- `LocalLoveNote` — text, active state, creation source, and selection history
- `RemoteEnvelope` — encrypted payload and cryptographic metadata while awaiting reveal
- `ProcessedRemoteMessage` — message ID and processing timestamp for deduplication
- `Countdown` — title, target time, optional recurrence, and display preferences
- `MoodCheckIn` — selected manual state, optional note, and local timestamp
- `AssetPack` — pack identity, version, manifest path, attribution, and selected state

The SQLite database is protected by normal Windows user-profile access controls but is not presented as a fully encrypted database. Remote private keys, device capability tokens, and other secret credential material are protected separately with Windows Data Protection API scoped to the current user. Remote note plaintext is not persisted by default.

Backup and restore cover the SQLite database, user-created local notes, preferences, and compatible custom asset packs. Secret tokens and machine-bound private keys are not portable; restoring on another PC requires pairing again.

## 11. Remote-note system

### 11.1 Components

The remote system consists of:

- One Cloudflare Worker written in TypeScript
- A D1 database
- Static mobile sender assets served by the Worker
- The desktop polling client

No public account system is required. Access is capability-based and scoped to one registered desktop device.

### 11.2 Device registration and pairing

1. The desktop generates a P-256 key pair locally.
2. The private key is protected with current-user DPAPI and never leaves the PC.
3. The desktop registers the public key with the Worker over HTTPS.
4. The Worker issues a random device identifier, a desktop polling capability token, and a one-time pairing code.
5. The pairing code expires after ten minutes and can be redeemed once.
6. The sender enters the code on the mobile page.
7. The Worker rate-limits attempts, consumes the code, creates an opaque sender session, and returns the desktop public key.
8. The browser stores only a Secure, HttpOnly, SameSite=Strict session cookie; scripts cannot read the session token.

The desktop can revoke all sender sessions, rotate its polling token, or generate a new pairing code at any time. Disconnecting from the sender page revokes that sender session only.

### 11.3 Message encryption

Each message uses a fresh ephemeral P-256 key pair in the browser:

1. Browser Web Crypto performs ECDH using the ephemeral private key and desktop public key.
2. HKDF-SHA-256 derives a 256-bit message key using a random salt and protocol-specific context string.
3. AES-256-GCM encrypts a versioned JSON payload using a fresh 96-bit nonce.
4. The envelope contains the message ID, protocol version, ephemeral public key, salt, nonce, ciphertext, authentication tag, creation time, and optional server-visible delivery time.

The encrypted payload contains the note text and optional reaction. The Worker validates envelope sizes and formats but cannot decrypt the payload.

### 11.4 Relay lifecycle

1. The sender uploads an encrypted envelope to its paired device.
2. D1 stores ciphertext, routing metadata, creation time, delivery time, and expiry.
3. The desktop polls with its bearer capability token using capped exponential backoff with jitter.
4. Eligible envelopes are returned in bounded batches.
5. The desktop validates and decrypts each envelope, records its message ID transactionally, and acknowledges device delivery.
6. The Worker removes the ciphertext after acknowledgment while retaining only the minimum short-lived delivery status needed by the sender page.
7. Undelivered envelopes expire and are deleted after 30 days.

`Delivered` means delivered to the desktop application, not opened by the recipient. The desktop never reports note-open events.

### 11.5 Conceptual HTTP interface

- `POST /v1/devices/register` — register a new desktop public key and issue desktop credentials
- `POST /v1/devices/current/rotate-key` — replace the public key using authenticated desktop credentials
- `POST /v1/devices/pairing-code` — create a short-lived one-time code
- `POST /v1/pairings/redeem` — redeem a code and create the sender session
- `GET /v1/sender/device` — return paired-device display metadata and public key
- `POST /v1/messages` — submit an encrypted envelope
- `GET /v1/messages` — retrieve eligible envelopes for the desktop
- `POST /v1/messages/{messageId}/ack` — acknowledge device delivery
- `GET /v1/messages/{messageId}/status` — return queued, delivered, expired, or failed
- `POST /v1/sender/disconnect` — revoke the current sender session
- `DELETE /v1/devices/current` — revoke a desktop device and purge its queued data

All mutating endpoints enforce origin checks where applicable, strict content types, payload size limits, rate limits, and unpredictable identifiers. API responses must not echo plaintext or cryptographic secrets into logs.

### 11.6 Sender page

The sender page is mobile-first and intentionally narrow in scope. After pairing, it offers:

- Message text entry
- Optional scheduled delivery time
- Wave, heart, hug, or celebrate reaction
- Local plaintext preview
- Send action with clear queued or failed state
- Recent relay status without read receipts
- Disconnect action

It does not contain recipient history, desktop activity, task data, reminders, mood data, or a remote-control interface.

## 12. Asset system and Dudu artwork

The initial private asset pack may be assembled from online Dudu/Bubu artwork and animations. The character is attributed online to Huang Xiao B; the creator site should be retained in the source manifest. Candidate discovery sources include the creator's site, Tenor animations, and sticker-pack mirrors.

Each imported asset records:

- Original source URL
- Access date
- Known creator attribution
- Original filename and checksum
- Transformations such as cropping, transparency cleanup, resizing, and frame extraction
- Semantic animation assignment
- Private-use-only status

The normalization pipeline removes backgrounds where necessary, trims transparent bounds without changing animation anchors, converts frames to a consistent color format, and generates thumbnails and reduced-motion poses.

The application must not depend permanently on a particular scraped URL. Normalized assets are bundled locally, and the manifest abstracts their filenames from behavior. A later original or licensed pack can replace the private pack without changing application logic.

The private installer is for the user and his girlfriend only. The app, asset pack, installer, and repository are not to be published or distributed publicly while using third-party Dudu artwork with uncertain redistribution rights.

Reference starting points:

- Creator: <https://huangxiaob.com/>
- Tenor idle/smile example: <https://tenor.com/view/dudu-cute-smile-photo-gif-3117522263724372729>
- Tenor thumbs-up example: <https://tenor.com/view/bubu-dudu-gif-25349847>
- Tenor waiting example: <https://tenor.com/view/dudu-waiting-dudu-cute-in-love-gif-14672583372493863103>
- Tenor hug example: <https://tenor.com/view/bubu-dudu-gif-13961388528266603475>
- Sticker-pack mirror with creator credit: <https://getstickerpack.com/stickers/xiao-xiong-yi-er-he-bu-bu-credits-to-huang-xiaob>

## 13. Notifications, fullscreen behavior, and quiet hours

The application attempts to register Windows app notifications for reminders and generic remote-note arrival. Sensitive text is excluded from notification bodies and lock-screen previews.

Fullscreen suppression applies to unsolicited pet bubbles, ambient animations that noticeably move the pet, remote-note arrival surfaces, and non-urgent reminders. Explicit user interactions remain available. Because fullscreen detection is imperfect across games and protected video players, the tray menu and global shortcut always provide a manual pause fallback.

Quiet hours define a local-time interval that may cross midnight. The user chooses whether each reminder should wait until quiet hours end or bypass them. Remote notes queue silently. On leaving quiet hours, Dudu reveals queued items gradually rather than presenting several bubbles simultaneously.

## 14. Failure and recovery behavior

### 14.1 Network outage

Remote polling backs off without showing repeated errors. The tray or Connection page may show an unobtrusive offline status. Local functionality remains unchanged. Sender uploads that fail remain visibly unsent in that browser until retried.

### 14.2 Sleep, resume, and clock changes

On resume, services recalculate timers from persisted absolute times. The reminder engine reconciles missed occurrences according to each reminder's policy. It must not replay every missed hydration interval after a long sleep.

### 14.3 Display changes

When a monitor disappears or its work area changes, Dudu moves to the nearest valid position on the primary monitor. Placement persists using monitor identity plus normalized coordinates, not raw pixels alone.

### 14.4 Asset failure

A missing or invalid frame causes the animation engine to use the bundled fallback idle pose. One broken optional animation must not prevent application startup.

### 14.5 Database failure

Failed migrations roll back transactionally. The application offers restoration from the newest valid automatic backup. If recovery is impossible, it preserves the damaged database for diagnosis before creating a clean database; it does not silently overwrite it.

### 14.6 Notification failure

If Windows notification registration or delivery is unavailable, the pet bubble and in-app inbox remain the source of truth. Reminder completion is persisted only after an explicit action, never inferred from notification disappearance.

## 15. Privacy and security requirements

- No analytics or telemetry SDKs
- No plaintext remote messages in Worker, D1, HTTP logs, crash logs, or notification payloads
- No private encryption keys leave the desktop
- No secrets embedded in the desktop binary or sender JavaScript
- DPAPI protection for desktop capabilities and private-key material
- HTTPS-only remote API and sender page
- Secure, HttpOnly, SameSite=Strict sender session cookie
- One-time pairing codes with ten-minute expiry, attempt limits, and rate limiting
- Maximum message size and bounded queue size per device
- Idempotent message processing and acknowledgment
- Thirty-day maximum retention for undelivered ciphertext
- Recipient-controlled sender revocation and complete remote-data deletion
- No read receipts
- No screen contents, application names, keystrokes, browsing activity, or inferred mood collection
- Backup export clearly explains which local data it contains

The encryption design protects message contents from an honest-but-curious relay and from accidental database disclosure. It does not claim to protect a message after either endpoint device or browser session is compromised.

## 16. Accessibility

- Full settings functionality through keyboard navigation
- Visible focus indicators
- Screen-reader labels for controls and status
- High-contrast-compatible settings surfaces
- Text scaling support without clipped controls
- Reduced-motion mode available during onboarding and settings
- No information conveyed by animation or color alone
- Pet actions duplicated in the tray or settings so precise pointer interaction is not mandatory
- Configurable global shortcut
- Notification and bubble durations that do not force rapid responses

## 17. Performance targets

Measured on a representative Windows 11 x64 laptop:

- Pet visible within three seconds of launch or sign-in under normal conditions
- Average idle CPU below 1% after five minutes of inactivity
- Average CPU below 3% during normal animation
- Working set below 200 MB during normal operation
- Approximately 15 FPS default sprite animation without unbounded timer drift
- No keyboard-focus theft from foreground applications
- No unbounded frame, bitmap, event, or database growth over an eight-hour run
- Remote notes delivered to an online desktop within 30 seconds under normal network conditions

These are release gates, not aspirational metrics. A debug build may exceed memory and startup targets; the signed-off release build may not.

## 18. Testing strategy

### 18.1 Unit tests

Unit coverage must include:

- State priority, interruption, and suppression rules
- Quiet hours spanning midnight
- Recurring reminder calculation and daylight-saving boundaries
- Snooze, completion, and missed-occurrence reconciliation
- Task and focus-session transitions
- Countdown calculations
- Manual mood-history summaries and local-only storage boundaries
- Local-note daily limits and repetition avoidance
- Remote-message deduplication and idempotent acknowledgment
- Envelope validation, key derivation, encryption, decryption, and tamper rejection
- Asset-manifest validation and fallback selection
- Database migrations and recovery decisions

### 18.2 Integration tests

Integration coverage must include:

- SQLite repositories and transactional migrations
- DPAPI secret round trips under the current Windows user
- Layered-window frame presentation through a minimal Windows harness
- Worker registration, pairing, authentication, upload, polling, acknowledgment, expiry, and revocation
- D1 data lifecycle
- Retry behavior under simulated timeouts and duplicate responses
- Backup and restore compatibility

### 18.3 UI automation

Automated UI flows cover:

- First-run onboarding
- Settings navigation
- Reminder and task creation
- Starting and ending a focus session
- Pairing and disconnecting a sender
- Theme, quiet-hours, privacy, and reduced-motion settings
- Backup and restore prompts

### 18.4 Manual Windows acceptance matrix

Release validation includes:

- Clean Windows 11 24H2 installation
- 100%, 125%, 150%, and 200% display scaling
- One and multiple monitors, including monitor removal
- Sleep, resume, lock, and unlock
- Explorer restart
- Fullscreen games and fullscreen video
- Launch at sign-in
- Notification permission granted and unavailable
- Transparent hit testing, dragging, resizing, and focus behavior
- Offline launch and extended network outage
- Eight-hour idle stability run
- Install, upgrade, uninstall, backup, and restore

## 19. Distribution and update model

Version 1 uses a self-contained x64 release installed per user by an Inno Setup installer. The installer creates Start menu and optional desktop shortcuts, registers uninstall information, and offers launch-at-sign-in without requesting elevation.

Automatic self-update is not included in version 1. Updates are installed manually over the existing per-user installation. The application migrates compatible data forward and creates a backup before migration.

Because this is a private unsigned application, Windows SmartScreen may show a warning on first installation. Purchasing a commercial code-signing certificate and public reputation building are outside version 1.

Uninstalling offers a clear choice between preserving personal data for reinstall and deleting all local data. Remote device deletion is offered separately when network connectivity is available.

## 20. Release acceptance criteria

Version 1 is complete only when all of the following are true:

1. A non-technical recipient can install the app and complete onboarding in under two minutes without administrator rights.
2. Dudu renders with transparent edges, animates, drags, resizes, survives DPI changes, and never steals keyboard focus.
3. Reminders, tasks, focus sessions, countdowns, comfort mode, outfits, and local notes work without internet access.
4. Quiet hours, pause modes, reduced motion, and fullscreen suppression consistently gate unsolicited behavior.
5. A sender can pair once from a phone and retain a secure session.
6. The sender can transmit an encrypted note or reaction, and the desktop normally receives it within 30 seconds while online.
7. Cloudflare receives no remote-note plaintext, and notification surfaces reveal no note text before recipient action.
8. Duplicate network delivery never creates duplicate visible notes.
9. Settings and local data survive restarts, application upgrades, sleep, and display-layout changes.
10. A broken nonessential asset falls back safely instead of preventing startup.
11. Automated test suites pass, and the complete manual Windows acceptance matrix passes on a clean system.
12. The private installer and uninstaller work on a clean Windows 11 x64 machine.

## 21. Implementation boundaries established by this design

The implementation plan must preserve these decisions:

- Windows 11 x64 only, with no macOS compatibility layer
- WinUI 3 settings plus a Win32 layered overlay window
- One local-first C# desktop application with SQLite
- Cloudflare Worker, D1, and a private mobile sender page
- Polling rather than Azure/WNS push infrastructure
- Browser-side end-to-end encryption using ephemeral ECDH, HKDF, and AES-GCM
- DPAPI-protected desktop secrets
- Generic note-arrival notification text and no read receipts
- Deterministic pet-state priority
- Replaceable manifest-driven private asset pack
- Per-user unpackaged installer with no administrator requirement
- No AI, surveillance, analytics, public accounts, or public distribution

Any change to these boundaries requires an explicit design revision before implementation.

## 22. Technical references

- .NET support policy: <https://dotnet.microsoft.com/en-us/platform/support/policy>
- Windows application windowing overview: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/windowing-overview>
- Windows App SDK application structure: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/windows-app-sdk-app-structure>
- Windows 11 materials and Mica: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/materials>
- Win32 layered windows: <https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features>
- Windows app notifications: <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/>

# Privacy boundaries

What Dudu Desktop stores, where, whether it ever leaves the PC, how long it lives, how a person
deletes it, and whether it rides along in a backup. This is the honest version of that story, not
the marketing one — see "The honest boundary" at the end before trusting any of the rows below.

Paths below assume the default data root, `%LocalAppData%\DuduDesktop` (overridable via the
`DUDU_DATA_ROOT` environment variable — see `src/Dudu.App/Hosting/AppPaths.cs`). The SQLite file
at `Database` is the single source of truth for every local entity except secrets and backups
themselves.

## Local entities

| Entity | Where | Leaves the PC? | Retention | Deletion path | In backups? |
| --- | --- | --- | --- | --- | --- |
| Profile (recipient name, onboarding flag) | `profiles` table, `Database` (`dudu.db`) | No | Until deleted | Settings → delete my data (`LocalDataMaintenanceService.DeleteAllUserDataAsync`; with a relay configured the remote device delete runs first, and if the relay cannot be reached nothing is wiped unless she chooses "wipe this pc only") | Yes |
| Preferences (theme, reduced motion, sounds, pet size and behavior, tray pause) | `preferences` table, `Database` | No | Until deleted | Same as above | Yes |
| Pet placement per monitor | `pet_placements` table, `Database` | No | Until deleted | Same as above | Yes |
| Data left by features this build no longer has (reminders + occurrence history, tasks, focus sessions, mood check-ins, countdowns, seeded or hand-written local notes and their display history) | `reminders`, `reminder_occurrences`, `tasks`, `focus_sessions`, `mood_check_ins`, `countdowns`, `local_note_history` tables and non-`remote-` rows of `local_notes`, `Database` | No | Dormant: this build never reads, shows, or writes them, and no migration drops them, so rows from an older install stay until deleted | Settings → delete my data (the wipe still clears every one of these tables) | Yes — while they exist, automatic backups carry them like any other row |
| A received private note, still encrypted | `remote_envelopes` table, `Database` (`ciphertext`, `ephemeral_public_key`, `nonce`, `authentication_tag`, `hkdf_salt` — all opaque ciphertext/key material, never plaintext) | Arrived from the relay, ciphertext only | Kept until the note is **revealed** on the Love Notes page, which saves it and consumes the envelope in one transaction (`CompanionFeatureTransactionService.SaveRemoteNoteAndConsumeEnvelopeAsync` → `RemoteEnvelopeRepository.TryConsumeAsync`). Acknowledging the relay does **not** delete it: acknowledgment only tells the relay to drop its copy. A note that is never opened keeps its ciphertext row indefinitely, and there is deliberately **no retention sweep** — silently deleting an unopened note would be data loss | Revealing the note, or delete-local-data | Yes — any backup snapshot taken while a received note is still unopened contains its row. It is ciphertext, never plaintext, but it does persist across backups until the note is revealed |
| Record that a private note was already processed | `processed_remote_messages` table, `Database` (message ID + timestamp only, no content) | No | Until deleted | Delete-local-data | Yes |
| A private note's decrypted text | Decrypted in memory (`RemoteSyncService.RevealAsync`/`EnvelopeCrypto.Decrypt`) when the recipient reveals it on the Love Notes page (`LoveNotesViewModel.RevealRemoteNoteAsync`), and **immediately persisted**: revealing writes the plaintext into `local_notes` as `remote-<messageId>` in the same transaction that consumes the envelope, so she can re-read it from the "opened notes" list. There is no separate save step and no way to read a note without keeping it | No | Until she deletes it from "opened notes" (`ILocalNoteRepository.DeleteAsync`) or deletes her data | Delete it from "opened notes", or Settings → delete my data | Yes — every automatic pre-migration backup taken while a revealed note exists contains its plaintext. Deleting it from "opened notes" removes it from the live database only; older backup copies age out through backup pruning, and delete-my-data removes every backup |
| Asset pack selection | `asset_packs` table, `Database` | No | Until deleted | Delete-local-data | Yes |
| Desktop's own ECDH private key (`desktop-ecdh-private-v1`) | `Secrets\desktop-ecdh-private-v1.bin`, DPAPI-protected (`CurrentUser` scope) via `DpapiSecretStore` | No — only the matching *public* key is ever sent to the relay | Until rotated or deleted | Delete-local-data deletes every `*.bin` under `Secrets` | No — DPAPI ties the blob to the Windows user profile, so a backup copy would not decrypt under a different profile/machine anyway, and the maintenance sweep deletes it before it could be swept into a future backup |
| Relay device ID and bearer token (`relay-device-id-v1`, `relay-desktop-token-v1`) | `Secrets\*.bin`, DPAPI-protected | The token is presented to the relay on every authenticated call (that is its purpose); it is never logged (see `PrivacySafeLog`) | Until rotated, revoked, or deleted | Delete-local-data, or `DELETE /v1/devices/current` against the relay | No (same DPAPI reasoning as above) |
| Database backups | `Backups\*.db` (`DatabaseBackupService`) | No | Rolling; pruned by the backup service's own retention policy | Delete-local-data deletes every `*.db` under `Backups` | N/A (this *is* the backup copy) — note a backup is a full snapshot, so it carries whatever plaintext rows above existed at snapshot time |
| Application logs | `Logs\diagnostics.log` (continuous sink, `src/Dudu.App/Hosting/FileDiagnosticLogger.cs`) and `Logs\startup-failure.log` (last-chance crash records, `StartupFailureLogger.cs`); the `Logs` directory is created on startup (`AppHost`) | No — local files only, never uploaded | Rolling caps trimmed to the most recent entries: ~128KB for `diagnostics.log`, 64KB for `startup-failure.log` | Manual deletion of the `Logs` directory (`LocalDataMaintenanceService.DeleteAllUserDataAsync` does not sweep logs) | No — log files are not part of database backups |
| Relay: device identity, pairing codes, sender sessions | Cloudflare D1, `devices`/`pairing_codes`/`sender_sessions` tables (`relay/migrations/0001_identity.sql`, plus the one-time-redemption winner marker in `0003_pairing_winner.sql`) — only *hashes* of tokens/codes, never the raw value | Yes, by definition (it is the relay's own database) | Pairing codes and stale sessions are swept hourly (`relay/src/cleanup.ts`). The `devices` row itself is **never** swept: `cleanup.ts` has no `devices` sweep at all, so the row (public key + token hash) persists for the lifetime of the D1 database | `DELETE /v1/devices/current` revokes the device and deletes its sessions, pairing codes, and queued messages, but **keeps** the `devices` row (public key + token hash) in a revoked state — that soft revoke is what makes the old bearer token answer 401 forever instead of being reusable. There is no endpoint or sweep that hard-deletes the row | N/A — this is server-side state, not a PC backup |
| Relay: queued message ciphertext + delivery status | D1 `messages`/`message_status` tables (`relay/migrations/0002_messages.sql`) — ciphertext and routing metadata only, never plaintext, never a read receipt | Yes (it is in transit) | `messages` rows are deleted on acknowledgment or after `MESSAGE_RETENTION_DAYS` (30 days); `message_status` rows expire on their own 24-hour clock | Desktop acknowledgment (`POST /v1/messages/:id/ack`), or the hourly sweep | N/A |

## What is deliberately never written anywhere

- The text of a private note is never written to a log. It *is* written to the database
  (`local_notes`) the moment the recipient reveals it, because revealing a note keeps it in her
  "opened notes" until she deletes it — see the "A private note's decrypted text" row above.
- Bearer tokens, device IDs used as secrets, pairing codes, and key material are never passed to a
  logger. `src/Dudu.Infrastructure/Logging/PrivacySafeLog.cs` is the single logging surface for
  the relay client and remote-sync path; every message it emits is a fixed, code-controlled string
  plus a status code, a byte count, or an internal category label — never request/response body
  content, never a token, never note text. The desktop file sink (`FileDiagnosticLogger`) is held
  to the same rule: it reuses `StartupFailureLogger.Redact`, so URLs and token-shaped secrets are
  redacted before anything reaches `diagnostics.log`, and its entries are fixed categories, levels,
  and exception details — never note plaintext, tokens, pairing codes, keys, ciphertext, or
  request/response bodies.
- The relay (`relay/`) never stores plaintext note text, ever: it only ever sees and stores
  ciphertext it has no key to decrypt.

## The honest boundary

- **Endpoint compromise can reveal an opened note.** While a note is decrypted and on screen (or
  briefly held in memory during that reveal), anything with the ability to read this process's
  memory — malware running as the same Windows user, a kernel-level compromise, physical access to
  an unlocked session — can see it. End-to-end encryption protects the note in transit and at rest
  on the relay; it cannot protect against a compromised endpoint at the moment the recipient
  themselves is meant to read it. Nothing in this design claims otherwise.
- **The relay cannot decrypt ciphertext encrypted to the desktop's real key.** The relay
  (Cloudflare Worker + D1) never holds the desktop's private key, never receives plaintext, and
  has no code path that would let it derive the note's content from what it stores. A full dump of
  D1 yields ciphertext, hashes, and routing metadata — never a note's text. This is a boundary
  enforced by what keys exist where (ECDH P-256 + HKDF-SHA-256 + AES-256-GCM, desktop-held private
  key only — see `protocol/README.md` and
  `src/Dudu.Infrastructure/Crypto/EncryptedEnvelope.cs`), not by a promise the relay makes about
  its own behavior. The assumption this rests on is stated plainly: the sender page encrypts to
  whichever public key the relay hands it, so a relay that substituted its own key at pairing time
  could read everything sent afterward. The sender pins the key it saw when it paired
  (`sender-src/pairing.ts`, `verifyStoredSession`) and drops to an explicit "key changed, pair
  again" warning rather than silently re-encrypting to a new one — which catches a later swap, not
  a relay that was already lying at the moment of first pairing.
- **A relay outage never blocks local functionality.** Every local-only entity above is
  presented entirely locally; `AppHost` starts the local presentation gateway (and its 30-second
  presentation tick) before it ever attempts to start remote sync, and a remote-sync failure
  is caught and reported, never allowed to stop the host (see `tests/Dudu.Infrastructure.Tests/Security/PrivacyBoundaryTests.cs`,
  `Worker_outage_does_not_stop_local_presentations`).

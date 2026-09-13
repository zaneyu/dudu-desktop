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
| Profile (recipient name, onboarding flag) | `profiles` table, `Database` (`dudu.db`) | No | Until deleted | Settings → delete local data (`LocalDataMaintenanceService.DeleteAllUserDataAsync`) | Yes |
| Preferences (theme, quiet hours, pet behavior) | `preferences` table, `Database` | No | Until deleted | Same as above | Yes |
| Pet placement per monitor | `pet_placements` table, `Database` | No | Until deleted | Same as above | Yes |
| Reminders + occurrence history | `reminders`, `reminder_occurrences` tables, `Database` | No | Until deleted | Same as above | Yes |
| Tasks | `tasks` table, `Database` | No | Until completed record is pruned or deleted | Same as above | Yes |
| Focus sessions | `focus_sessions` table, `Database` | No | Until deleted | Same as above | Yes |
| Local notes (the bundled/custom love-note pool) and their display history | `local_notes`, `local_note_history` tables, `Database` | No | Until deleted | Same as above | Yes |
| Mood check-ins (choice + optional free-text note) | `mood_check_ins` table, `Database` | No | Until deleted | Same as above | Yes |
| Countdowns | `countdowns` table, `Database` | No | Until deleted | Same as above | Yes |
| A received private note, still encrypted | `remote_envelopes` table, `Database` (`ciphertext`, `ephemeral_public_key`, `nonce`, `authentication_tag`, `hkdf_salt` — all opaque ciphertext/key material, never plaintext) | Arrived from the relay, ciphertext only | Deleted the moment it is acknowledged (`RemoteEnvelopeRepository.TryConsumeAsync`); never lingers past that | Automatic on acknowledgment, or via delete-local-data | Only if a backup snapshot happens to land between arrival and acknowledgment — the row is ciphertext, not plaintext, even then |
| Record that a private note was already processed | `processed_remote_messages` table, `Database` (message ID + timestamp only, no content) | No | Until deleted | Delete-local-data | Yes |
| A private note's decrypted text | Nowhere on disk — decrypted in memory only (`RemoteSyncService`/`EnvelopeCrypto.Decrypt`) for the duration of a reveal, then discarded when the process exits or the view model releases it | No | Not persisted at all | N/A — there is nothing to delete | No |
| Asset pack selection | `asset_packs` table, `Database` | No | Until deleted | Delete-local-data | Yes |
| Desktop's own ECDH private key (`desktop-ecdh-private-v1`) | `Secrets\desktop-ecdh-private-v1.bin`, DPAPI-protected (`CurrentUser` scope) via `DpapiSecretStore` | No — only the matching *public* key is ever sent to the relay | Until rotated or deleted | Delete-local-data deletes every `*.bin` under `Secrets` | No — DPAPI ties the blob to the Windows user profile, so a backup copy would not decrypt under a different profile/machine anyway, and the maintenance sweep deletes it before it could be swept into a future backup |
| Relay device ID and bearer token (`relay-device-id-v1`, `relay-desktop-token-v1`) | `Secrets\*.bin`, DPAPI-protected | The token is presented to the relay on every authenticated call (that is its purpose); it is never logged (see `PrivacySafeLog`) | Until rotated, revoked, or deleted | Delete-local-data, or `DELETE /v1/devices/current` against the relay | No (same DPAPI reasoning as above) |
| Database backups | `Backups\*.db` (`DatabaseBackupService`) | No | Rolling; pruned by the backup service's own retention policy | Delete-local-data deletes every `*.db` under `Backups` | N/A (this *is* the backup copy) — note a backup is a full snapshot, so it carries whatever plaintext rows above existed at snapshot time |
| Application logs | `Logs` directory is created on startup (`AppHost`), but as of this task no on-disk log-file provider is wired up — diagnostics go through `ILoggerFactory`/`Trace` only (debugger/ETW), not a persisted file. If a future task adds a file sink here, it must use `PrivacySafeLog` (below) exclusively. | No | N/A today | N/A today | N/A today |
| Relay: device identity, pairing codes, sender sessions | Cloudflare D1, `devices`/`pairing_codes`/`sender_sessions` tables (`relay/migrations/0001_identity.sql`) — only *hashes* of tokens/codes, never the raw value | Yes, by definition (it is the relay's own database) | Pairing codes and stale sessions are swept hourly (`relay/src/cleanup.ts`); devices persist until the user deletes the device | `DELETE /v1/devices/current`, or the hourly sweep for expired codes/sessions | N/A — this is server-side state, not a PC backup |
| Relay: queued message ciphertext + delivery status | D1 `messages`/`message_status` tables (`relay/migrations/0002_messages.sql`) — ciphertext and routing metadata only, never plaintext, never a read receipt | Yes (it is in transit) | `messages` rows are deleted on acknowledgment or after `MESSAGE_RETENTION_DAYS` (30 days); `message_status` rows expire on their own 24-hour clock | Desktop acknowledgment (`POST /v1/messages/:id/ack`), or the hourly sweep | N/A |

## What is deliberately never written anywhere

- The text of a private note, once decrypted, is held only in memory for as long as it is on
  screen. It is never written to the database, a log, or a backup.
- Bearer tokens, device IDs used as secrets, pairing codes, and key material are never passed to a
  logger. `src/Dudu.Infrastructure/Logging/PrivacySafeLog.cs` is the single logging surface for
  the relay client and remote-sync path; every message it emits is a fixed, code-controlled string
  plus a status code, a byte count, or an internal category label — never request/response body
  content, never a token, never note text.
- The relay (`relay/`) never stores plaintext note text, ever: it only ever sees and stores
  ciphertext it has no key to decrypt.

## The honest boundary

- **Endpoint compromise can reveal an opened note.** While a note is decrypted and on screen (or
  briefly held in memory during that reveal), anything with the ability to read this process's
  memory — malware running as the same Windows user, a kernel-level compromise, physical access to
  an unlocked session — can see it. End-to-end encryption protects the note in transit and at rest
  on the relay; it cannot protect against a compromised endpoint at the moment the recipient
  themselves is meant to read it. Nothing in this design claims otherwise.
- **The relay cannot decrypt valid ciphertext.** The relay (Cloudflare Worker + D1) never holds the
  desktop's private key, never receives plaintext, and has no code path that would let it derive
  the note's content from what it stores. A full dump of D1 yields ciphertext, hashes, and routing
  metadata — never a note's text. This is a boundary enforced by what keys exist where (ECDH
  P-256 + HKDF-SHA-256 + AES-256-GCM, desktop-held private key only — see
  `relay/protocol/README.md` and `src/Dudu.Infrastructure/Crypto/EncryptedEnvelope.cs`), not by
  a promise the relay makes about its own behavior.
- **A relay outage never blocks local functionality.** Reminders, tasks, focus sessions, and every
  other local-only entity above are scheduled and presented entirely locally; `AppHost` ticks the
  local reminder service before it ever attempts to start remote sync, and a remote-sync failure
  is caught and reported, never allowed to stop the host (see `tests/Dudu.Infrastructure.Tests/Security/PrivacyBoundaryTests.cs`,
  `Worker_outage_does_not_stop_local_reminders`).

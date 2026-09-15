# Security model

This covers how remote notes are protected between the sender page and the desktop. It is
written for a private app with one desktop and one paired sender. For what is stored locally
and what is never written, see [privacy.md](privacy.md).

## Parties and what each one is trusted with

| Party | Trusted for | Not trusted for |
|---|---|---|
| Desktop (Windows user account) | The ECDH private key, the relay bearer token, decrypted note text | Nothing is hidden from other processes that run as the same Windows user |
| Relay (Cloudflare Worker + D1) | Storing ciphertext, enforcing sender sessions, rate limits, deliver-after | Reading notes, or vouching for who wrote a note |
| Sender page (browser) | Encrypting to the desktop's public key | Anything the desktop does beyond storing and showing the note |

## Envelope cryptography

- The desktop has a static P-256 ECDH key pair. The private key is stored with DPAPI
  (CurrentUser) in a directory whose ACL is limited to the current user.
- Each note uses a fresh ephemeral sender key. HKDF-SHA256 turns the shared secret into a
  message key, and AES-GCM encrypts the payload with that key.
- The AAD is `protocolVersion|messageId|createdUtc|deliverAfterUtc`. Changing any of these
  fields breaks decryption.
- Secrets held in memory (shared secret, message key, plaintext bytes, PKCS#8 copies, token
  bytes) are zeroed right after use. This is defence in depth, not isolation: the bearer header
  and the returned note strings are managed strings and stay in memory until garbage collection.

## Trust model and known limitations

- **No sender signatures.** AES-GCM proves only that whoever encrypted the note held the
  desktop's public key and that nobody changed the note afterwards. The relay allows posting
  only with a valid sender session, but the desktop cannot verify who the sender is. Every
  payload field is untrusted: kind and reaction must be on an allow-list, text and ciphertext
  have size limits, and the desktop only stores and displays a note, never acts on its content.
- **Static key, no forward secrecy for the desktop.** If the desktop private key leaks, every
  note still stored anywhere can be read. "Disconnect senders" rotates the relay token and
  revokes sessions, but deliberately keeps the key pair so stored notes stay readable.
- **Replay window.** A note's `createdUtc` must be no more than 5 minutes ahead of the desktop
  clock and no more than 30 days old when it is received. Duplicate message IDs are removed
  locally through `processed_remote_messages`. When a stored note is revealed later, it is
  checked against the time it was received, not the current time.
- **Early delivery.** The relay holds back scheduled notes until their deliver-after time. If a
  skewed clock delivers one early, the desktop stores it but does not acknowledge it, notify
  about it, or list it until it is due.
- **Junk from a valid session.** Any holder of a sender session can post notes that will not
  decrypt. The desktop checks cheap lengths before doing any EC work, skips envelopes that are
  too large, and keeps only the first few undecryptable envelopes per UTC day. Beyond that it
  acknowledges and drops them without storing them. The relay also limits each session's
  submissions per hour.
- **Same-user malware.** DPAPI CurrentUser and the directory ACL protect against other Windows
  users and offline disk access. They do not protect against code running as the same user.
  Deleting a secret overwrites the file with zeros first, but SSDs and filesystem journals can
  keep old copies.

## Credential lifecycle

- **Registration.** The desktop token is written before the device ID. The device ID marks
  registration as complete, so a registration that stops part-way is repeated on the next start.
  A staged token left from an earlier device is removed before registering.
- **Token rotation.** The new token is written to a staging slot first and then activated. If
  activation fails, the next 401 promotes the staged token and retries once. If staging itself
  fails, the local registration is deleted and the desktop registers again.
- **Unpair.** The relay device is deleted, then the local device ID, token and staged token. A
  404 or 401 from the relay means the device is already gone, so local cleanup still happens.
  This is how you recover from "needs repair". The ECDH key is destroyed only when the caller
  asks for it explicitly, because destroying it makes every stored, unrevealed note unreadable.

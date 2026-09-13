# Dudu Desktop Companion — version-one encrypted note protocol

This document specifies the wire contract for encrypted notes exchanged between a sender
(a browser, via `relay/sender-src/crypto.ts`) and the desktop companion (C#, via
`src/Dudu.Infrastructure/Crypto/EnvelopeCrypto.cs`). Both implementations must match this
specification byte-for-byte; they are covered by bidirectional interop tests (see
`tests/Dudu.Infrastructure.Tests/Crypto/EnvelopeCryptoTests.cs` and `relay/test/crypto.spec.ts`)
that prove a real envelope produced by one runtime decrypts correctly in the other.

**This protocol is for private, personal use between a known sender and a single desktop.** It
authenticates the *ciphertext* (via AES-GCM) but does not authenticate the *sender's identity* —
anyone who has the desktop's public key can construct a valid envelope. It is not intended as a
general-purpose secure messaging protocol.

## Schemas

- [`message-envelope.schema.json`](./message-envelope.schema.json) — `EncryptedEnvelopeV1`, the
  wire envelope.
- [`message-payload.schema.json`](./message-payload.schema.json) — `RemoteMessagePayloadV1`, the
  plaintext JSON encrypted inside the envelope's `ciphertext` field.

Both schemas express decoded-Base64URL-length limits (e.g. `hkdfSalt` decoding to exactly 32
bytes, `ciphertext` decoding to 17–6144 bytes) and the `text` field's 2000-Unicode-scalar-value
limit only in their `description` prose, since JSON Schema's `minLength`/`maxLength` keywords
count UTF-16 code units of the *encoded string*, not decoded bytes or Unicode scalar values. A
schema-only validator does not enforce these; implementations must enforce them in code, the way
`EnvelopeCrypto.Decrypt` (C#) and `validateEnvelopeShape`/`decryptPayloadForTest` (TypeScript) do.

## Wire envelope

```typescript
interface EncryptedEnvelopeV1 {
  protocolVersion: 1;
  messageId: string;          // canonical lowercase UUID, e.g. crypto.randomUUID()
  createdUtc: string;         // ISO-8601 UTC, "Z" suffix
  deliverAfterUtc: string | null; // ISO-8601 UTC, "Z" suffix, or null
  ephemeralPublicKey: string; // Base64URL(SPKI DER P-256 public key), unpadded
  hkdfSalt: string;           // Base64URL(32 random bytes), unpadded
  nonce: string;              // Base64URL(12 random bytes), unpadded
  ciphertext: string;         // Base64URL(AES-256-GCM ciphertext ‖ 16-byte tag), unpadded
}
```

All binary fields are **unpadded Base64URL** (RFC 4648 §5, no `=` padding). `.NET` uses
`System.Buffers.Text.Base64Url`; TypeScript hand-rolls the codec from `atob`/`btoa` (see
"Runtime neutrality" below).

## Decrypted payload

The `ciphertext` field, once decrypted, is UTF-8 JSON matching:

```typescript
interface RemoteMessagePayloadV1 {
  kind: "note";
  text: string;                                                    // <= 2000 Unicode scalar values
  reaction: "none" | "wave" | "heart" | "hug" | "celebrate";
}
```

Unknown keys in either the envelope or the decrypted payload are rejected.

## Key agreement and derivation

1. The desktop generates a long-lived P-256 ECDH key pair on first use
   (`DesktopKeyService`), storing the PKCS#8 private key under `ISecretStore` key
   `desktop-ecdh-private-v1` (DPAPI-backed on Windows) and publishing the SPKI public key
   (Base64URL) to senders.
2. For every message, the sender generates a **fresh ephemeral P-256 key pair** and includes its
   SPKI public key as `ephemeralPublicKey`. Ephemeral keys are never reused across messages.
3. ECDH raw shared secret = the 32-byte X9.62 shared secret (the x-coordinate) from
   `ECDiffieHellman.DeriveRawSecretAgreement` (.NET) / `crypto.subtle.deriveBits({name:"ECDH",
   public}, ephemeralPrivateKey, 256)` (Web Crypto).
4. HKDF-SHA-256 derives the 32-byte AES key from that shared secret, using:
   - `salt` = the envelope's 32-byte random `hkdfSalt`
   - `info` = UTF-8 bytes of the literal string `DuduDesktop:message:v1`
   - output length = 32 bytes

   In Web Crypto, this is a **two-step import + deriveKey**: `importKey("raw", sharedSecretBits,
   "HKDF", false, ["deriveKey"])` followed by `deriveKey({name:"HKDF", hash:"SHA-256", salt,
   info}, hkdfKeyMaterial, {name:"AES-GCM", length:256}, false, ["encrypt","decrypt"])`. Do
   **not** attempt to go directly from the ECDH-derived key to an HKDF `deriveKey` call in one
   step — at time of writing this fails on Safari.

## Encryption

AES-256-GCM with:
- a fresh random 12-byte nonce (`nonce`)
- a 128-bit (16-byte) authentication tag
- wire ciphertext = `ciphertext ‖ tag` (this is Web Crypto's native output shape; .NET's
  `AesGcm` API splits the trailing 16 bytes off into a separate tag parameter before calling
  `Decrypt`)

### Additional authenticated data (AAD)

AAD = UTF-8 bytes of:

```
{protocolVersion}|{messageId}|{createdUtc}|{deliverAfterUtc-or-empty}
```

using the **wire strings verbatim** — i.e. exactly the `messageId`/`createdUtc`/`deliverAfterUtc`
strings as they appear in the JSON envelope, with no timestamp reparsing or renormalization
before building the AAD, and `deliverAfterUtc` replaced with an empty string when `null`. This
means any tampering with `messageId`, `createdUtc`, or `deliverAfterUtc` after encryption breaks
AES-GCM tag verification on decrypt, without needing separate signature checks on those fields.

## Size and format limits

| Field / value | Constraint |
| --- | --- |
| `protocolVersion` | must equal `1` |
| `messageId` | canonical lowercase UUID: `^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$` |
| `createdUtc` | must parse as a timestamp; must not be more than 5 minutes in the future |
| `ephemeralPublicKey` | must decode to a valid P-256 SPKI DER public key (91 bytes) |
| `hkdfSalt` | must decode to exactly 32 bytes |
| `nonce` | must decode to exactly 12 bytes |
| `ciphertext` | decoded length must be between 17 and 6144 bytes inclusive (tag alone is 16 bytes, so 17 is the minimum for 1 byte of plaintext) |
| decrypted payload | must not exceed 4096 UTF-8 bytes |
| payload `kind` | must equal `"note"` |
| payload `text` | must not exceed 2000 Unicode scalar values (code points, not UTF-16 code units) |
| payload `reaction` | must be one of `none`, `wave`, `heart`, `hug`, `celebrate` |
| envelope / payload keys | unknown keys are rejected in both objects |

Both `EnvelopeCrypto.Decrypt` (C#) and `decryptPayloadForTest` (TypeScript) enforce every rule
above. A failure to authenticate the ciphertext (a tampered tag, nonce, or AAD) raises a
authentication-specific error (`CryptographicException` / `AuthenticationTagMismatchException` in
.NET, `EnvelopeDecryptionError` in TypeScript); every other rule above raises a distinct
shape/validation error (`EnvelopeValidationException` in .NET, `EnvelopeValidationError` in
TypeScript) so callers can tell "this envelope is malformed" apart from "this ciphertext was
tampered with."

## Runtime neutrality (TypeScript)

`relay/sender-src/crypto.ts` is the single TypeScript implementation of this contract and must
run unmodified in a browser, in Node, and in a Cloudflare Worker. It therefore:

- uses only the Web Crypto API (`crypto.subtle`, `crypto.getRandomValues`) plus `atob`/`btoa` —
  no `Buffer`, no `process`, no DOM-only globals (`document`, `window`, `fetch`);
- exports `validateEnvelopeShape(envelope)`, which checks every rule above **except** those that
  require the private key (payload decryption, `kind`, `reaction`, `text` length, unknown payload
  keys). This is deliberate: Task 18's relay Worker never holds the desktop's private key, so it
  can only validate envelope *shape* before forwarding, not decrypt and validate the payload. Full
  decryption + payload validation lives in `decryptPayloadForTest`, named for its purpose (proving
  the TypeScript side of the contract in tests) rather than for production use.

`relay/package.json`'s `engines.node` pin is the plan's target host version, not a hard runtime
requirement; a newer Node (as on this dev machine) still runs everything correctly and only emits
a non-fatal `EBADENGINE` warning during `npm install`/`npm ci`.

## Interop proof

This protocol is proven bidirectionally, with both directions actually executed (not skipped) on
a development host that has both a .NET SDK and Node available:

- **TypeScript → C#**: `relay/test/crypto.spec.ts` generates a recipient key pair, encrypts an
  envelope, and invokes `tools/Dudu.CryptoInterop` (`decrypt --private <pkcs8-file> --envelope
  <json-file>`) as a subprocess, asserting its stdout matches the original payload.
- **C# → TypeScript**: `EnvelopeCryptoTests.Decrypt_interoperates_with_an_envelope_produced_by_the_node_fixture`
  invokes `relay/dist/src/protocol/encrypt-fixture.js` (built from `relay/src/protocol/encrypt-fixture.ts`)
  as a subprocess and decrypts its stdout with `EnvelopeCrypto.Decrypt`.

## Private-use notice

This code and protocol are built for a single individual's private desktop companion app, are not
independently audited, and make no claims of suitability for any use beyond that. Do not reuse
this as a general-purpose encryption library.

/**
 * The ONE runtime-neutral implementation of the version-one encrypted-note wire contract. This
 * file must run unmodified in a browser, in Node, and in a Cloudflare Worker: no `Buffer`, no
 * `process`, no DOM-only globals (`document`, `window`, `fetch`). It only uses the Web Crypto API
 * (`crypto.subtle`, `crypto.getRandomValues`) and `atob`/`btoa`, all of which exist in every one
 * of those runtimes.
 *
 * Mirrors src/Dudu.Infrastructure/Crypto/EnvelopeCrypto.cs byte-for-byte. See protocol/README.md
 * for the specification both implementations follow.
 */

import {
  ALLOWED_REACTIONS,
  HKDF_INFO,
  HKDF_SALT_LENGTH,
  MAXIMUM_CIPHERTEXT_LENGTH,
  MAXIMUM_CREATED_UTC_SKEW_MINUTES,
  MAXIMUM_PAYLOAD_UTF8_BYTES,
  MAXIMUM_TEXT_SCALAR_VALUES,
  MESSAGE_ID_PATTERN,
  MINIMUM_CIPHERTEXT_LENGTH,
  NONCE_LENGTH,
  PROTOCOL_VERSION,
  TAG_LENGTH,
  type EncryptedEnvelopeV1,
  type Reaction,
  type RemoteMessagePayloadV1,
} from "../src/protocol/types.js";

// @types/node's ambient Uint8Array augmentation defaults its generic parameter to
// ArrayBufferLike (SharedArrayBuffer included), which no longer structurally matches
// lib.dom.d.ts's BufferSource (Uint8Array<ArrayBuffer>) under TypeScript's newer typed-array
// generics. This file is compiled both with and without "node" in `types`, so every byte buffer
// that reaches a Web Crypto call is funneled through `toBytes` to keep it typed consistently.
type Bytes = Uint8Array<ArrayBuffer>;

function toBytes(value: Uint8Array): Bytes {
  return value as Bytes;
}

/** Raised when an envelope or its decrypted payload does not match the wire contract. */
export class EnvelopeValidationError extends Error {}

/** Raised specifically when AES-GCM authentication-tag verification fails. */
export class EnvelopeDecryptionError extends Error {}

export interface TestKeyPair {
  publicKey: CryptoKey;
  privateKey: CryptoKey;
}

export interface EncryptMetadata {
  messageId: string;
  createdUtc?: string;
  deliverAfterUtc?: string | null;
}

/** Generates a fresh P-256 ECDH key pair whose private key is extractable, for tests only. */
export async function createRecipientForTest(): Promise<TestKeyPair> {
  const keyPair = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" },
    true,
    ["deriveBits"],
  );
  return { publicKey: keyPair.publicKey as CryptoKey, privateKey: keyPair.privateKey as CryptoKey };
}

/** Imports a recipient's P-256 SPKI public key, Base64URL encoded, for encryption. */
export async function importRecipientPublicKey(spkiBase64Url: string): Promise<CryptoKey> {
  const bytes = decodeBase64UrlOrThrow(spkiBase64Url, "publicKey");
  return crypto.subtle.importKey("spki", bytes, { name: "ECDH", namedCurve: "P-256" }, false, []);
}

/**
 * Encrypts `payload` for `recipientPublicKey` using a fresh ephemeral key pair, producing a
 * version-one envelope ready to go over the wire.
 */
export async function encryptPayload(
  recipientPublicKey: CryptoKey,
  payload: RemoteMessagePayloadV1,
  metadata: EncryptMetadata,
): Promise<EncryptedEnvelopeV1> {
  const ephemeralKeyPair = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" },
    true,
    ["deriveBits"],
  );

  const sharedSecretBits = await crypto.subtle.deriveBits(
    { name: "ECDH", public: recipientPublicKey } as EcdhKeyDeriveParams,
    ephemeralKeyPair.privateKey as CryptoKey,
    256,
  );

  const salt = toBytes(crypto.getRandomValues(new Uint8Array(HKDF_SALT_LENGTH)));
  const aesKey = await deriveAesKey(sharedSecretBits, salt);

  const createdUtc = metadata.createdUtc ?? new Date().toISOString();
  const deliverAfterUtc = metadata.deliverAfterUtc ?? null;
  const nonce = toBytes(crypto.getRandomValues(new Uint8Array(NONCE_LENGTH)));
  const aad = buildAdditionalAuthenticatedData({
    protocolVersion: PROTOCOL_VERSION,
    messageId: metadata.messageId,
    createdUtc,
    deliverAfterUtc,
  });

  const payloadBytes = toBytes(new TextEncoder().encode(JSON.stringify(payload)));
  const ciphertextAndTag = await crypto.subtle.encrypt(
    { name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: TAG_LENGTH * 8 },
    aesKey,
    payloadBytes,
  );

  const ephemeralPublicKeySpki = await crypto.subtle.exportKey(
    "spki",
    ephemeralKeyPair.publicKey as CryptoKey,
  );

  return {
    protocolVersion: PROTOCOL_VERSION,
    messageId: metadata.messageId,
    createdUtc,
    deliverAfterUtc,
    ephemeralPublicKey: bytesToBase64Url(new Uint8Array(ephemeralPublicKeySpki)),
    hkdfSalt: bytesToBase64Url(salt),
    nonce: bytesToBase64Url(nonce),
    ciphertext: bytesToBase64Url(new Uint8Array(ciphertextAndTag)),
  };
}

/**
 * Best-effort zeroing of a plaintext payload buffer once it is no longer needed. JavaScript
 * strings themselves cannot be reliably zeroed, and `encryptPayload` above encodes its own
 * internal copy of the payload bytes that this cannot reach — so this only shortens how long a
 * *caller-held* copy of the plaintext bytes stays resident in memory. It is not a guarantee
 * against a compromised browser endpoint.
 */
export function zeroPayloadBytes(bytes: Uint8Array): void {
  bytes.fill(0);
}

/**
 * Validates the SHAPE of an envelope — everything that can be checked without the recipient's
 * private key: protocolVersion, messageId, createdUtc, the ephemeral key's validity, and the
 * salt/nonce/ciphertext lengths. Deliberately does NOT decrypt, so Task 18's relay Worker (which
 * never holds the desktop's private key) can reuse this to reject malformed envelopes before
 * forwarding them.
 */
export async function validateEnvelopeShape(envelope: unknown): Promise<EncryptedEnvelopeV1> {
  if (typeof envelope !== "object" || envelope === null) {
    throw new EnvelopeValidationError("Envelope must be an object.");
  }

  const candidate = envelope as Record<string, unknown>;
  const allowedKeys = new Set([
    "protocolVersion",
    "messageId",
    "createdUtc",
    "deliverAfterUtc",
    "ephemeralPublicKey",
    "hkdfSalt",
    "nonce",
    "ciphertext",
  ]);
  for (const key of Object.keys(candidate)) {
    if (!allowedKeys.has(key)) {
      throw new EnvelopeValidationError(`Unknown envelope key '${key}'.`);
    }
  }

  if (candidate.protocolVersion !== PROTOCOL_VERSION) {
    throw new EnvelopeValidationError(`Unsupported protocolVersion '${String(candidate.protocolVersion)}'.`);
  }

  if (typeof candidate.messageId !== "string" || !MESSAGE_ID_PATTERN.test(candidate.messageId)) {
    throw new EnvelopeValidationError("messageId is not a canonical lowercase UUID.");
  }

  if (typeof candidate.createdUtc !== "string") {
    throw new EnvelopeValidationError("createdUtc must be a string.");
  }
  const createdUtcMs = Date.parse(candidate.createdUtc);
  if (Number.isNaN(createdUtcMs)) {
    throw new EnvelopeValidationError("createdUtc is not a parsable timestamp.");
  }
  if (createdUtcMs > Date.now() + MAXIMUM_CREATED_UTC_SKEW_MINUTES * 60_000) {
    throw new EnvelopeValidationError("createdUtc is too far in the future.");
  }

  if (candidate.deliverAfterUtc !== null && typeof candidate.deliverAfterUtc !== "string") {
    throw new EnvelopeValidationError("deliverAfterUtc must be null or a string.");
  }

  if (typeof candidate.ephemeralPublicKey !== "string") {
    throw new EnvelopeValidationError("ephemeralPublicKey must be a string.");
  }
  const ephemeralPublicKeyBytes = decodeBase64UrlOrThrow(candidate.ephemeralPublicKey, "ephemeralPublicKey");
  try {
    await crypto.subtle.importKey(
      "spki",
      ephemeralPublicKeyBytes,
      { name: "ECDH", namedCurve: "P-256" },
      false,
      [],
    );
  } catch {
    throw new EnvelopeValidationError("ephemeralPublicKey is not a valid P-256 SPKI key.");
  }

  if (typeof candidate.hkdfSalt !== "string") {
    throw new EnvelopeValidationError("hkdfSalt must be a string.");
  }
  const salt = decodeBase64UrlOrThrow(candidate.hkdfSalt, "hkdfSalt");
  if (salt.length !== HKDF_SALT_LENGTH) {
    throw new EnvelopeValidationError(`hkdfSalt must be ${HKDF_SALT_LENGTH} bytes.`);
  }

  if (typeof candidate.nonce !== "string") {
    throw new EnvelopeValidationError("nonce must be a string.");
  }
  const nonce = decodeBase64UrlOrThrow(candidate.nonce, "nonce");
  if (nonce.length !== NONCE_LENGTH) {
    throw new EnvelopeValidationError(`nonce must be ${NONCE_LENGTH} bytes.`);
  }

  if (typeof candidate.ciphertext !== "string") {
    throw new EnvelopeValidationError("ciphertext must be a string.");
  }
  const ciphertext = decodeBase64UrlOrThrow(candidate.ciphertext, "ciphertext");
  if (ciphertext.length < MINIMUM_CIPHERTEXT_LENGTH || ciphertext.length > MAXIMUM_CIPHERTEXT_LENGTH) {
    throw new EnvelopeValidationError(
      `ciphertext must be between ${MINIMUM_CIPHERTEXT_LENGTH} and ${MAXIMUM_CIPHERTEXT_LENGTH} bytes.`,
    );
  }

  return candidate as unknown as EncryptedEnvelopeV1;
}

/**
 * Decrypts and fully validates a version-one envelope. Named "ForTest" because production code
 * never runs this path: the relay Worker only ever calls {@link validateEnvelopeShape} (it has no
 * private key), and the desktop app's decryption lives in C# (`EnvelopeCrypto.Decrypt`). This
 * function exists so the TypeScript side of the contract can be exercised and proven in tests.
 */
export async function decryptPayloadForTest(
  recipientPrivateKey: CryptoKey,
  envelope: EncryptedEnvelopeV1,
): Promise<RemoteMessagePayloadV1> {
  const validated = await validateEnvelopeShape(envelope);

  const ephemeralPublicKeyBytes = decodeBase64UrlOrThrow(validated.ephemeralPublicKey, "ephemeralPublicKey");
  const ephemeralPublicKey = await crypto.subtle.importKey(
    "spki",
    ephemeralPublicKeyBytes,
    { name: "ECDH", namedCurve: "P-256" },
    false,
    [],
  );

  const sharedSecretBits = await crypto.subtle.deriveBits(
    { name: "ECDH", public: ephemeralPublicKey } as EcdhKeyDeriveParams,
    recipientPrivateKey,
    256,
  );

  const salt = decodeBase64UrlOrThrow(validated.hkdfSalt, "hkdfSalt");
  const aesKey = await deriveAesKey(sharedSecretBits, salt);

  const nonce = decodeBase64UrlOrThrow(validated.nonce, "nonce");
  const ciphertextAndTag = decodeBase64UrlOrThrow(validated.ciphertext, "ciphertext");
  const aad = buildAdditionalAuthenticatedData(validated);

  let plaintext: ArrayBuffer;
  try {
    plaintext = await crypto.subtle.decrypt(
      { name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: TAG_LENGTH * 8 },
      aesKey,
      ciphertextAndTag,
    );
  } catch {
    throw new EnvelopeDecryptionError("Authentication tag verification failed.");
  }

  if (plaintext.byteLength > MAXIMUM_PAYLOAD_UTF8_BYTES) {
    throw new EnvelopeValidationError(`Decrypted payload exceeds ${MAXIMUM_PAYLOAD_UTF8_BYTES} UTF-8 bytes.`);
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(new TextDecoder().decode(plaintext));
  } catch {
    throw new EnvelopeValidationError("Decrypted payload is not valid JSON.");
  }

  if (typeof parsed !== "object" || parsed === null) {
    throw new EnvelopeValidationError("Decrypted payload must be an object.");
  }

  const payload = parsed as Record<string, unknown>;
  const allowedPayloadKeys = new Set(["kind", "text", "reaction"]);
  for (const key of Object.keys(payload)) {
    if (!allowedPayloadKeys.has(key)) {
      throw new EnvelopeValidationError(`Unknown payload key '${key}'.`);
    }
  }

  if (payload.kind !== "note") {
    throw new EnvelopeValidationError(`Unsupported payload kind '${String(payload.kind)}'.`);
  }

  if (typeof payload.text !== "string") {
    throw new EnvelopeValidationError("text must be a string.");
  }
  if (countUnicodeScalarValues(payload.text) > MAXIMUM_TEXT_SCALAR_VALUES) {
    throw new EnvelopeValidationError(`text exceeds ${MAXIMUM_TEXT_SCALAR_VALUES} Unicode scalar values.`);
  }

  if (typeof payload.reaction !== "string" || !isAllowedReaction(payload.reaction)) {
    throw new EnvelopeValidationError(`Unknown reaction '${String(payload.reaction)}'.`);
  }

  return { kind: "note", text: payload.text, reaction: payload.reaction };
}

function isAllowedReaction(value: string): value is Reaction {
  return (ALLOWED_REACTIONS as readonly string[]).includes(value);
}

async function deriveAesKey(sharedSecretBits: ArrayBuffer, salt: Bytes): Promise<CryptoKey> {
  // Two-step import + deriveKey (rather than deriving directly from the ECDH key) because some
  // Web Crypto implementations (Safari, at time of writing) do not support ECDH -> HKDF in one
  // deriveKey call.
  const hkdfKeyMaterial = await crypto.subtle.importKey("raw", sharedSecretBits, "HKDF", false, ["deriveKey"]);

  return crypto.subtle.deriveKey(
    { name: "HKDF", hash: "SHA-256", salt, info: toBytes(new TextEncoder().encode(HKDF_INFO)) },
    hkdfKeyMaterial,
    { name: "AES-GCM", length: 256 },
    false,
    ["encrypt", "decrypt"],
  );
}

function buildAdditionalAuthenticatedData(envelope: {
  protocolVersion: number;
  messageId: string;
  createdUtc: string;
  deliverAfterUtc: string | null;
}): Bytes {
  const deliverAfterUtc = envelope.deliverAfterUtc ?? "";
  return toBytes(new TextEncoder().encode(
    `${envelope.protocolVersion}|${envelope.messageId}|${envelope.createdUtc}|${deliverAfterUtc}`,
  ));
}

function decodeBase64UrlOrThrow(value: string, fieldName: string): Bytes {
  try {
    return base64UrlToBytes(value);
  } catch {
    throw new EnvelopeValidationError(`${fieldName} is not valid Base64URL.`);
  }
}

/** Hand-rolled so this file never needs Node's `Buffer`: works from atob/btoa alone. */
function bytesToBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i++) {
    binary += String.fromCharCode(bytes[i]);
  }
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function base64UrlToBytes(value: string): Bytes {
  if (!/^[A-Za-z0-9_-]*$/.test(value)) {
    throw new Error("Invalid Base64URL string.");
  }
  const base64 = value.replace(/-/g, "+").replace(/_/g, "/");
  const paddingNeeded = (4 - (base64.length % 4)) % 4;
  const padded = base64 + "=".repeat(paddingNeeded);
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return toBytes(bytes);
}

function countUnicodeScalarValues(text: string): number {
  let count = 0;
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  for (const _character of text) {
    count++;
  }
  return count;
}

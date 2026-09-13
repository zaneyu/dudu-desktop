/**
 * Version-one wire contract for encrypted notes exchanged between the sender (browser) and the
 * desktop companion (C#). Identical in shape and validation rules to
 * src/Dudu.Infrastructure/Crypto/EncryptedEnvelope.cs — see protocol/README.md for the full
 * specification. Changing anything here requires a matching change there.
 */

export const PROTOCOL_VERSION = 1 as const;

/** HKDF-SHA-256 "info" parameter, shared by both runtimes. */
export const HKDF_INFO = "DuduDesktop:message:v1";

/** Random salt fed into HKDF, in bytes. */
export const HKDF_SALT_LENGTH = 32;

/** AES-256-GCM nonce length, in bytes. */
export const NONCE_LENGTH = 12;

/** AES-256-GCM authentication tag length, in bytes. */
export const TAG_LENGTH = 16;

/** Wire ciphertext is ciphertext‖tag, so the minimum is one plaintext byte plus the tag. */
export const MINIMUM_CIPHERTEXT_LENGTH = TAG_LENGTH + 1;

export const MAXIMUM_CIPHERTEXT_LENGTH = 6144;

export const MAXIMUM_PAYLOAD_UTF8_BYTES = 4096;

export const MAXIMUM_TEXT_SCALAR_VALUES = 2000;

export const MAXIMUM_CREATED_UTC_SKEW_MINUTES = 5;

/** Canonical lowercase UUID, matching what crypto.randomUUID() emits. */
export const MESSAGE_ID_PATTERN =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export const ALLOWED_REACTIONS = [
  "none",
  "wave",
  "heart",
  "hug",
  "celebrate",
] as const;

export type Reaction = (typeof ALLOWED_REACTIONS)[number];

export interface EncryptedEnvelopeV1 {
  protocolVersion: 1;
  messageId: string;
  createdUtc: string;
  deliverAfterUtc: string | null;
  ephemeralPublicKey: string;
  hkdfSalt: string;
  nonce: string;
  ciphertext: string;
}

export interface RemoteMessagePayloadV1 {
  kind: "note";
  text: string;
  reaction: Reaction;
}

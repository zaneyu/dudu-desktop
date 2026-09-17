/**
 * Server-side envelope validation for `POST /v1/messages`. Wraps `validateEnvelopeShape` from
 * `sender-src/crypto.ts` (the one runtime-neutral implementation of the wire contract) rather
 * than re-implementing any of its rules, and adds only what that function cannot check without
 * request-time context: the strict `createdUtc` Z shape and its `[now - 1h, now + 5min]`
 * acceptance window, the `deliverAfterUtc` scheduling cap and its must-not-precede-`createdUtc`
 * ordering, and the byte-size inspection needed to return 413 instead of 422 for oversized
 * fields.
 *
 * `validateEnvelopeShape` already enforces: protocol version, canonical UUID `messageId`,
 * `createdUtc` parsability and its five-minute future skew, Base64URL form, the 32-byte salt and
 * 12-byte nonce, the ciphertext's 17–6144 byte bounds, and that the ephemeral key is a valid
 * P-256 SPKI key (which bounds its plausible length on its own) — none of that is duplicated
 * here.
 */
import { EnvelopeValidationError, validateEnvelopeShape } from "../../sender-src/crypto.js";
import {
  MAXIMUM_CIPHERTEXT_LENGTH,
  MAXIMUM_EPHEMERAL_PUBLIC_KEY_BYTES,
  MESSAGE_RETENTION_DAYS,
  type EncryptedEnvelopeV1,
} from "../protocol/types.js";
import { base64UrlToBytes, bytesToBase64Url } from "./tokens.js";

/** Every Base64URL-encoded field in the wire contract, checked for canonical form below. */
const BASE64URL_FIELDS = ["ephemeralPublicKey", "hkdfSalt", "nonce", "ciphertext"] as const;

/**
 * A noncanonical Base64URL string decodes fine (it matches the charset `base64UrlToBytes`
 * accepts) but re-encoding the decoded bytes does not reproduce it byte-for-byte — e.g. trailing
 * padding bits set to something other than zero, or an alternate-but-decodable character run.
 * Genuinely invalid characters are a different, pre-existing failure mode (`base64UrlToBytes`
 * returns `null`, and `validateEnvelopeShape` already rejects that): this check only fires once a
 * value is known to decode, so it never duplicates that error.
 */
function isNoncanonicalBase64Url(value: string): boolean {
  const decoded = base64UrlToBytes(value);
  if (!decoded) {
    return false;
  }
  return bytesToBase64Url(decoded) !== value;
}

/** The request body was not JSON-shaped as an object at all — maps to 400. */
export class EnvelopeMalformedError extends Error {}

/** A field decoded to more bytes than the wire contract allows — maps to 413. */
export class EnvelopeTooLargeError extends Error {}

/** Any other shape or scheduling violation — maps to 422. */
export class EnvelopeSemanticError extends Error {}

const MAXIMUM_DELIVER_AFTER_MS = MESSAGE_RETENTION_DAYS * 24 * 60 * 60 * 1000;

/**
 * Strict `createdUtc` shape: full UTC ISO-8601 with a `Z` designator and optional fractional
 * seconds (what `Date.toISOString()` emits), e.g. `2026-01-02T03:04:05.678Z`. Loose
 * `Date.parse` inputs — date-only strings, offsets, missing timezone — are rejected even when
 * they denote a valid instant: a canonical wire timestamp must not depend on the parser's
 * fallback heuristics. `deliverAfterUtc` uses the same canonical UTC form so validation and the
 * SQLite delivery comparison operate on one unambiguous wire representation.
 */
const STRICT_ISO8601_Z_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,3})?Z$/;

/**
 * How far back a `createdUtc` may lie: one hour. Together with the five-minute future skew
 * below, the acceptance window is `[now - 1h, now + 5min]`. The future bound matches
 * `MAXIMUM_CREATED_UTC_SKEW_MINUTES` (already enforced inside `validateEnvelopeShape` against
 * `Date.now()`); the past bound is new here — a days-old `createdUtc` on a freshly submitted
 * envelope is either a replay or a broken clock, and expiry math anchored to it would silently
 * shorten (or, for `deliverAfterUtc`, distort) retention.
 */
const MAXIMUM_CREATED_UTC_AGE_MS = 60 * 60 * 1000;
const MAXIMUM_CREATED_UTC_SKEW_MS = 5 * 60 * 1000;

/**
 * Validates an incoming, not-yet-trusted request body as an `EncryptedEnvelopeV1`. Throws one of
 * the three error classes above; never returns a partially-validated envelope.
 */
export async function validateIncomingEnvelope(body: unknown, now: Date): Promise<EncryptedEnvelopeV1> {
  if (typeof body !== "object" || body === null || Array.isArray(body)) {
    throw new EnvelopeMalformedError("Envelope must be a JSON object.");
  }
  const candidate = body as Record<string, unknown>;

  // Inspect sizes BEFORE calling validateEnvelopeShape, so an oversized field is distinguished as
  // 413 by its byte length, never by parsing validateEnvelopeShape's error message text.
  if (typeof candidate.ciphertext === "string") {
    const decoded = base64UrlToBytes(candidate.ciphertext);
    if (decoded && decoded.length > MAXIMUM_CIPHERTEXT_LENGTH) {
      throw new EnvelopeTooLargeError("ciphertext exceeds the maximum allowed size.");
    }
  }
  if (typeof candidate.ephemeralPublicKey === "string") {
    const decoded = base64UrlToBytes(candidate.ephemeralPublicKey);
    if (decoded && decoded.length > MAXIMUM_EPHEMERAL_PUBLIC_KEY_BYTES) {
      throw new EnvelopeTooLargeError("ephemeralPublicKey exceeds the maximum allowed size.");
    }
  }

  // A noncanonical Base64URL value is a semantic problem (413 above is reserved for "too many
  // bytes"), so it is checked after the size bounds but before delegating to
  // `validateEnvelopeShape`.
  for (const field of BASE64URL_FIELDS) {
    const value = candidate[field];
    if (typeof value === "string" && isNoncanonicalBase64Url(value)) {
      throw new EnvelopeSemanticError(`${field} is not canonical Base64URL.`);
    }
  }

  let envelope: EncryptedEnvelopeV1;
  try {
    envelope = await validateEnvelopeShape(candidate);
  } catch (error) {
    if (error instanceof EnvelopeValidationError) {
      throw new EnvelopeSemanticError(error.message);
    }
    throw error;
  }

  if (envelope.deliverAfterUtc !== null) {
    if (!STRICT_ISO8601_Z_PATTERN.test(envelope.deliverAfterUtc)) {
      throw new EnvelopeSemanticError("deliverAfterUtc must be strict ISO-8601 UTC (Z) form.");
    }
    const deliverAfterMs = Date.parse(envelope.deliverAfterUtc);
    if (Number.isNaN(deliverAfterMs) || !isRealCalendarInstant(envelope.deliverAfterUtc, deliverAfterMs)) {
      throw new EnvelopeSemanticError("deliverAfterUtc must be a real ISO-8601 UTC instant.");
    }
    const createdMs = Date.parse(envelope.createdUtc);
    if (deliverAfterMs < createdMs) {
      throw new EnvelopeSemanticError("deliverAfterUtc is before createdUtc.");
    }
    if (deliverAfterMs > now.getTime() + MAXIMUM_DELIVER_AFTER_MS) {
      throw new EnvelopeSemanticError(`deliverAfterUtc is more than ${MESSAGE_RETENTION_DAYS} days ahead.`);
    }
  }

  // Request-time bounds `validateEnvelopeShape` cannot check without `now`: strict Z shape plus
  // the [now - 1h, now + 5min] acceptance window for `createdUtc`.
  if (!STRICT_ISO8601_Z_PATTERN.test(envelope.createdUtc)) {
    throw new EnvelopeSemanticError("createdUtc must be strict ISO-8601 UTC (Z) form.");
  }
  const createdMs = Date.parse(envelope.createdUtc);
  if (Number.isNaN(createdMs) || !isRealCalendarInstant(envelope.createdUtc, createdMs)) {
    throw new EnvelopeSemanticError("createdUtc must be strict ISO-8601 UTC (Z) form.");
  }
  if (
    createdMs < now.getTime() - MAXIMUM_CREATED_UTC_AGE_MS ||
    createdMs > now.getTime() + MAXIMUM_CREATED_UTC_SKEW_MS
  ) {
    throw new EnvelopeSemanticError("createdUtc is outside the acceptable clock window.");
  }

  return envelope;
}

/**
 * `Date.parse` rolls impossible fields over (`2026-02-30` becomes March 2, `T24:00:00` the next
 * day) instead of rejecting them. A pattern-matched value is a real instant only if formatting
 * the parsed time reproduces it, with the fraction padded to milliseconds.
 */
function isRealCalendarInstant(value: string, parsedMs: number): boolean {
  const [, fraction = ""] = /(?:\.(\d{1,3}))?Z$/.exec(value) ?? [];
  const canonical = `${value.slice(0, 19)}.${fraction.padEnd(3, "0")}Z`;
  return new Date(parsedMs).toISOString() === canonical;
}

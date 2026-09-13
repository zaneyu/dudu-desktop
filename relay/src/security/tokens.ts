/**
 * Capability-token, pairing-code, and hashing primitives. Workers-native only (Web Crypto +
 * `atob`/`btoa`): no Node APIs, since `wrangler.jsonc` intentionally omits `nodejs_compat`.
 *
 * Tokens and pairing codes are never persisted in the clear: callers store only the hashes this
 * module produces, and never pass a raw token/code to `console.*`.
 */

const TOKEN_BYTE_LENGTH = 32;

/** 8 characters, Crockford Base32 alphabet minus the visually ambiguous I, L, O, U. */
export const PAIRING_CODE_LENGTH = 8;
const PAIRING_CODE_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/** Generates a fresh 32-byte capability token, encoded as unpadded Base64URL. */
export function generateCapabilityToken(): string {
  return bytesToBase64Url(crypto.getRandomValues(new Uint8Array(TOKEN_BYTE_LENGTH)));
}

/** Generates an 8-character pairing code drawn uniformly from `PAIRING_CODE_ALPHABET`. */
export function generatePairingCode(): string {
  const randomBytes = crypto.getRandomValues(new Uint8Array(PAIRING_CODE_LENGTH));
  let code = "";
  for (let i = 0; i < PAIRING_CODE_LENGTH; i++) {
    // PAIRING_CODE_ALPHABET.length is 32 (a power of two), so byte % length is unbiased.
    code += PAIRING_CODE_ALPHABET[randomBytes[i] % PAIRING_CODE_ALPHABET.length];
  }
  return code;
}

/** Upper-cases and trims redemption input before it is hashed or length-checked. */
export function normalizePairingCodeInput(raw: string): string {
  return raw.trim().toUpperCase();
}

/** Hex-encoded SHA-256 digest of raw bytes. */
export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", toArrayBuffer(bytes));
  return bytesToHex(new Uint8Array(digest));
}

/** Hex-encoded SHA-256 digest of a UTF-8 string (capability tokens, rate-limit bucket keys). */
export async function sha256HexOfText(text: string): Promise<string> {
  return sha256Hex(new TextEncoder().encode(text));
}

/**
 * Hex-encoded HMAC-SHA-256 of `message`, keyed by `key`. Used to hash pairing codes with the
 * `PAIRING_CODE_PEPPER` Worker secret so a stolen D1 export alone cannot be brute-forced offline.
 */
export async function hmacSha256Hex(key: string, message: string): Promise<string> {
  const cryptoKey = await crypto.subtle.importKey(
    "raw",
    toArrayBuffer(new TextEncoder().encode(key)),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const signature = await crypto.subtle.sign("HMAC", cryptoKey, toArrayBuffer(new TextEncoder().encode(message)));
  return bytesToHex(new Uint8Array(signature));
}

/** Hand-rolled so this module never needs Node's `Buffer`: works from atob/btoa alone. */
export function bytesToBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i++) {
    binary += String.fromCharCode(bytes[i]);
  }
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function base64UrlToBytes(value: string): Uint8Array | null {
  if (!/^[A-Za-z0-9_-]*$/.test(value)) {
    return null;
  }
  const base64 = value.replace(/-/g, "+").replace(/_/g, "/");
  const paddingNeeded = (4 - (base64.length % 4)) % 4;
  const padded = base64 + "=".repeat(paddingNeeded);
  try {
    const binary = atob(padded);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
  } catch {
    return null;
  }
}

function bytesToHex(bytes: Uint8Array): string {
  return Array.from(bytes).map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function toArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
}

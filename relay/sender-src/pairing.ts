/**
 * Local pairing state. The relay's session cookie is HttpOnly (the browser never sees it), so
 * this module only ever persists the public key and device id `localStorage` needs to render the
 * paired state instantly on reload, and confirms the session is still live with the relay before
 * trusting that cache.
 */
import { disconnectSender as apiDisconnectSender, getSenderDevice, redeemPairing } from "./api.js";
import { importRecipientPublicKey } from "./crypto.js";
import {
  base64UrlToBytes,
  PAIRING_CODE_CHARSET_PATTERN,
  PAIRING_CODE_LENGTH,
  sha256Hex,
} from "../src/security/tokens.js";

const STORAGE_KEY = "dudu.sender.device.v1";

export interface StoredDevice {
  deviceId: string;
  publicKey: string;
  /** Optional only for pre-fingerprint localStorage records; new pairings always set it. */
  publicKeyFingerprint?: string;
}

function isStoredDevice(value: unknown): value is StoredDevice {
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const candidate = value as Record<string, unknown>;
  return (
    typeof candidate.deviceId === "string" &&
    typeof candidate.publicKey === "string" &&
    (candidate.publicKeyFingerprint === undefined || typeof candidate.publicKeyFingerprint === "string")
  );
}

export function loadStoredDevice(): StoredDevice | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return null;
    }
    const parsed: unknown = JSON.parse(raw);
    return isStoredDevice(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

function storeDevice(device: StoredDevice): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(device));
}

export function clearStoredDevice(): void {
  localStorage.removeItem(STORAGE_KEY);
}

/**
 * Cleans pairing-code input as the partner types or pastes it: drops spaces, dashes, and any
 * other separator a copied code might carry ("7K9M-2R4X", " 7k9m 2r4x "), upper-cases it, and
 * maps the Crockford look-alikes the code alphabet deliberately omits (O -> 0, I/L -> 1), so a
 * misread character still redeems instead of burning one of the relay's rate-limited attempts.
 * The result is capped at the code length. Pure, so the page can apply it on every keystroke.
 */
export function normalizePairingCodeTyping(raw: string): string {
  return raw
    .toUpperCase()
    .replace(/[^0-9A-Z]/g, "")
    .replace(/O/g, "0")
    .replace(/[IL]/g, "1")
    .slice(0, PAIRING_CODE_LENGTH);
}

/** Whether `code` (already normalized) is a well-formed pairing code worth sending at all. */
export function isWellFormedPairingCode(code: string): boolean {
  return PAIRING_CODE_CHARSET_PATTERN.test(code);
}

export async function pairWithCode(code: string): Promise<StoredDevice> {
  const result = await redeemPairing(code);
  const keyBytes = base64UrlToBytes(result.publicKey);
  if (!keyBytes) {
    throw new PairingAuthenticityError("The relay returned an invalid device key.");
  }
  try {
    await importRecipientPublicKey(result.publicKey);
  } catch {
    throw new PairingAuthenticityError("The relay returned an invalid device key.");
  }
  const localFingerprint = await sha256Hex(keyBytes);
  if (localFingerprint !== result.publicKeyFingerprint) {
    throw new PairingAuthenticityError("The relay returned a mismatched device fingerprint.");
  }
  const device: StoredDevice = {
    deviceId: result.deviceId,
    publicKey: result.publicKey,
    publicKeyFingerprint: localFingerprint,
  };
  storeDevice(device);
  return device;
}

export class PairingAuthenticityError extends Error {}

/**
 * The outcome of confirming a cached pairing: still good, gone, or -- the case review I6 added --
 * still live but answering with a different public key than the one pinned at pairing.
 */
export type StoredSessionState =
  | { state: "paired"; device: StoredDevice }
  | { state: "unpaired" }
  | { state: "key-changed" };

/**
 * On load, if a device is cached locally, confirms the session is still live AND that the relay
 * still reports the very public key this browser saw when it paired (trust on first use). A 401
 * clears the cache and reports `unpaired`; a changed key clears it and reports `key-changed`, for
 * the caller to show as a visible warning -- silently adopting the new key would let a relay that
 * swapped in its own key read every note from then on. Throws (cache intact) when the relay could
 * not be reached at all, so a transient failure never forces a fresh pairing.
 */
export async function verifyStoredSession(): Promise<StoredSessionState> {
  const stored = loadStoredDevice();
  if (!stored) {
    return { state: "unpaired" };
  }
  const current = await getSenderDevice();
  if (!current) {
    clearStoredDevice();
    return { state: "unpaired" };
  }
  const currentKeyBytes = base64UrlToBytes(current.publicKey);
  if (!currentKeyBytes) {
    clearStoredDevice();
    return { state: "key-changed" };
  }
  const currentFingerprint = await sha256Hex(currentKeyBytes);
  const storedFingerprint =
    stored.publicKeyFingerprint ??
    await sha256Hex(base64UrlToBytes(stored.publicKey) ?? new Uint8Array());
  if (
    current.publicKey !== stored.publicKey ||
    current.publicKeyFingerprint !== currentFingerprint ||
    storedFingerprint !== currentFingerprint ||
    current.deviceId !== stored.deviceId
  ) {
    clearStoredDevice();
    return { state: "key-changed" };
  }
  const upgraded = { ...stored, publicKeyFingerprint: currentFingerprint };
  if (stored.publicKeyFingerprint === undefined) {
    storeDevice(upgraded);
  }
  return { state: "paired", device: upgraded };
}

export async function disconnect(): Promise<void> {
  await apiDisconnectSender();
  clearStoredDevice();
}

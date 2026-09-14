/**
 * Local pairing state. The relay's session cookie is HttpOnly (the browser never sees it), so
 * this module only ever persists the public key and device id `localStorage` needs to render the
 * paired state instantly on reload, and confirms the session is still live with the relay before
 * trusting that cache.
 */
import { disconnectSender as apiDisconnectSender, getSenderDevice, redeemPairing } from "./api.js";

const STORAGE_KEY = "dudu.sender.device.v1";

export interface StoredDevice {
  deviceId: string;
  publicKey: string;
}

function isStoredDevice(value: unknown): value is StoredDevice {
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const candidate = value as Record<string, unknown>;
  return typeof candidate.deviceId === "string" && typeof candidate.publicKey === "string";
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

export async function pairWithCode(code: string): Promise<StoredDevice> {
  const result = await redeemPairing(code);
  const device: StoredDevice = { deviceId: result.deviceId, publicKey: result.publicKey };
  storeDevice(device);
  return device;
}

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
  if (current.publicKey !== stored.publicKey) {
    clearStoredDevice();
    return { state: "key-changed" };
  }
  return { state: "paired", device: { deviceId: stored.deviceId, publicKey: stored.publicKey } };
}

export async function disconnect(): Promise<void> {
  await apiDisconnectSender();
  clearStoredDevice();
}

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
 * On load, if a device is cached locally, confirms the session is still live. Clears the cache
 * and returns null on a 401; returns null with the cache intact when nothing is stored or the
 * relay could not be reached (leaves the door open for the caller to retry rather than forcing a
 * fresh pairing over a transient failure).
 */
export async function verifyStoredSession(): Promise<StoredDevice | null> {
  const stored = loadStoredDevice();
  if (!stored) {
    return null;
  }
  const current = await getSenderDevice();
  if (!current) {
    clearStoredDevice();
    return null;
  }
  return { deviceId: stored.deviceId, publicKey: current.publicKey };
}

export async function disconnect(): Promise<void> {
  await apiDisconnectSender();
  clearStoredDevice();
}

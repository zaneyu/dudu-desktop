import { importRecipientPublicKey } from "../../sender-src/crypto.js";
import {
  buildDeleteDeviceStatements,
  buildRotateKeyStatements,
  countActiveSenderSessions,
  findDeviceByTokenHash,
  insertDevice,
  type DeviceRecord,
} from "../db/devices.js";
import { insertPairingCode } from "../db/pairings.js";
import type { Env } from "../env.js";
import { readJsonBody, UnsupportedMediaTypeError } from "../http/body.js";
import {
  badRequest,
  jsonResponse,
  noContentResponse,
  tooManyRequests,
  unauthorized,
  unsupportedMediaType,
} from "../http/responses.js";
import { enforceRateLimit, clientIpFromRequest } from "../security/rateLimit.js";
import {
  base64UrlToBytes,
  generateCapabilityToken,
  generatePairingCode,
  hmacSha256Hex,
  sha256Hex,
  sha256HexOfText,
} from "../security/tokens.js";

const REGISTER_RATE_LIMIT_PER_HOUR = 5;
const PAIRING_CODE_VALIDITY_MS = 10 * 60_000;

/** Validates `publicKey` as a Base64URL-encoded P-256 SPKI public key, without importing it for
 * any cryptographic use — the Worker only ever stores and forwards it, never derives keys. */
async function parsePublicKeyOrThrow(publicKey: unknown): Promise<string> {
  if (typeof publicKey !== "string" || publicKey.length === 0) {
    throw new Error("publicKey is required.");
  }
  // Throws if publicKey is not valid Base64URL or not a P-256 SPKI public key.
  await importRecipientPublicKey(publicKey);
  return publicKey;
}

/** Authenticates a device by its `Authorization: Bearer <desktopToken>` header. */
export async function authenticateDevice(request: Request, env: Env): Promise<DeviceRecord | null> {
  const header = request.headers.get("Authorization") ?? "";
  const match = /^Bearer (.+)$/.exec(header);
  if (!match) {
    return null;
  }
  const desktopTokenHash = await sha256HexOfText(match[1]);
  return findDeviceByTokenHash(env.DB, desktopTokenHash);
}

export async function registerDevice(request: Request, env: Env): Promise<Response> {
  const allowed = await enforceRateLimit(
    env.DB,
    "register-device",
    clientIpFromRequest(request),
    REGISTER_RATE_LIMIT_PER_HOUR,
  );
  if (!allowed) {
    return tooManyRequests();
  }

  let body: unknown;
  try {
    body = await readJsonBody(request);
  } catch (error) {
    if (error instanceof UnsupportedMediaTypeError) {
      return unsupportedMediaType();
    }
    return badRequest();
  }

  let publicKey: string;
  try {
    const { publicKey: rawPublicKey } = (body ?? {}) as { publicKey?: unknown };
    publicKey = await parsePublicKeyOrThrow(rawPublicKey);
  } catch {
    return badRequest("publicKey must be a Base64URL-encoded P-256 SPKI public key.");
  }

  const now = new Date();
  const nowIso = now.toISOString();
  const deviceId = crypto.randomUUID();
  const desktopToken = generateCapabilityToken();
  const desktopTokenHash = await sha256HexOfText(desktopToken);

  await insertDevice(env.DB, { id: deviceId, publicKeySpki: publicKey, desktopTokenHash, createdUtc: nowIso });

  const pairingCode = generatePairingCode();
  const pairingCodeHash = await hmacSha256Hex(env.PAIRING_CODE_PEPPER, pairingCode);
  const pairingCodeExpiresUtc = new Date(now.getTime() + PAIRING_CODE_VALIDITY_MS).toISOString();
  await insertPairingCode(env.DB, {
    codeHash: pairingCodeHash,
    deviceId,
    expiresUtc: pairingCodeExpiresUtc,
  });

  return jsonResponse({ deviceId, desktopToken, pairingCode, pairingCodeExpiresUtc }, 201);
}

export async function getCurrentDevice(request: Request, env: Env): Promise<Response> {
  const device = await authenticateDevice(request, env);
  if (!device) {
    return unauthorized();
  }
  const nowIso = new Date().toISOString();
  const keyBytes = base64UrlToBytes(device.publicKeySpki);
  if (!keyBytes) {
    // Validated as SPKI Base64URL at registration time; a stored row that fails to decode here is
    // stored data corruption, not a client error.
    throw new Error("Stored device public key is not valid Base64URL.");
  }
  const publicKeyFingerprint = await sha256Hex(keyBytes);
  const activeSenderSessions = await countActiveSenderSessions(env.DB, device.id, nowIso);
  return jsonResponse({ createdUtc: device.createdUtc, publicKeyFingerprint, activeSenderSessions });
}

export async function rotateDeviceKey(request: Request, env: Env): Promise<Response> {
  const device = await authenticateDevice(request, env);
  if (!device) {
    return unauthorized();
  }

  let body: unknown;
  try {
    body = await readJsonBody(request);
  } catch (error) {
    if (error instanceof UnsupportedMediaTypeError) {
      return unsupportedMediaType();
    }
    return badRequest();
  }

  let newPublicKey: string;
  try {
    const { publicKey: rawPublicKey } = (body ?? {}) as { publicKey?: unknown };
    newPublicKey = await parsePublicKeyOrThrow(rawPublicKey);
  } catch {
    return badRequest("publicKey must be a Base64URL-encoded P-256 SPKI public key.");
  }

  const newDesktopToken = generateCapabilityToken();
  const newDesktopTokenHash = await sha256HexOfText(newDesktopToken);
  const revokedUtc = new Date().toISOString();

  // Task 18 appends a `DELETE FROM messages WHERE device_id = ?` statement to this same batch
  // (via buildRotateKeyStatements) once the messages table exists, keeping rotation atomic.
  await env.DB.batch(
    buildRotateKeyStatements(env.DB, {
      deviceId: device.id,
      newPublicKeySpki: newPublicKey,
      newDesktopTokenHash,
      revokedUtc,
    }),
  );

  return jsonResponse({ desktopToken: newDesktopToken });
}

export async function createDevicePairingCode(request: Request, env: Env): Promise<Response> {
  const device = await authenticateDevice(request, env);
  if (!device) {
    return unauthorized();
  }
  const code = generatePairingCode();
  const codeHash = await hmacSha256Hex(env.PAIRING_CODE_PEPPER, code);
  const expiresUtc = new Date(Date.now() + PAIRING_CODE_VALIDITY_MS).toISOString();
  await insertPairingCode(env.DB, { codeHash, deviceId: device.id, expiresUtc });
  return jsonResponse({ code, expiresUtc }, 201);
}

/**
 * Permanently revokes a device and deletes everything tied to it: queued ciphertext, message
 * status rows, sender sessions, and unredeemed pairing codes — all in one atomic `db.batch`, so a
 * desktop that has been unpaired leaves nothing behind for its former senders to keep polling.
 */
export async function deleteCurrentDevice(request: Request, env: Env): Promise<Response> {
  const device = await authenticateDevice(request, env);
  if (!device) {
    return unauthorized();
  }
  await env.DB.batch(buildDeleteDeviceStatements(env.DB, device.id, new Date().toISOString()));
  return noContentResponse();
}

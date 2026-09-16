import { importRecipientPublicKey } from "../../sender-src/crypto.js";
import {
  buildDeleteDeviceStatements,
  buildRotateKeyStatements,
  countActiveSenderSessions,
  findDeviceByTokenHash,
  insertDeviceStatement,
  type DeviceRecord,
} from "../db/devices.js";
import {
  deleteUnredeemedPairingCodesStatement,
  insertPairingCodeStatement,
} from "../db/pairings.js";
import type { Env } from "../env.js";
import { JsonBodyTooLargeError, readJsonBody, UnsupportedMediaTypeError } from "../http/body.js";
import {
  badRequest,
  jsonResponse,
  noContentResponse,
  payloadTooLarge,
  tooManyRequests,
  unauthorized,
  unsupportedMediaType,
} from "../http/responses.js";
import { clientIpFromRequest, enforceRateLimit, GLOBAL_RATE_LIMIT_KEY } from "../security/rateLimit.js";
import { isSecureTransport } from "../security/transport.js";
import {
  base64UrlToBytes,
  generateCapabilityToken,
  generatePairingCode,
  hmacSha256Hex,
  isPlausibleCapabilityToken,
  sha256Hex,
  sha256HexOfText,
} from "../security/tokens.js";

const REGISTER_RATE_LIMIT_PER_HOUR = 5;
/** IP-independent cap on registrations across all callers, so rotating source addresses cannot
 * mint unbounded device rows. A single-couple deployment registers a handful of times at most. */
const GLOBAL_REGISTER_RATE_LIMIT_PER_HOUR = 20;
/**
 * Per-device hourly caps, keyed by device id (not IP) via the existing rate-limit helper: mint
 * and rotation are authenticated actions, so the identity that benefits is the right unit to
 * throttle. Rotation is the tighter of the two — it revokes sessions and wipes the queue.
 */
const PAIRING_CODE_MINT_RATE_LIMIT_PER_HOUR = 10;
const ROTATE_KEY_RATE_LIMIT_PER_HOUR = 5;
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
  if (!isSecureTransport(request)) {
    return null;
  }
  const header = request.headers.get("Authorization") ?? "";
  const match = /^Bearer (.+)$/.exec(header);
  if (!match) {
    return null;
  }
  // Length/charset precheck before hashing: only a 43–44 character Base64URL value can be a
  // token this relay minted. Garbage never reaches the SHA-256 or the device lookup.
  if (!isPlausibleCapabilityToken(match[1])) {
    return null;
  }
  const desktopTokenHash = await sha256HexOfText(match[1]);
  return findDeviceByTokenHash(env.DB, desktopTokenHash);
}

export async function registerDevice(request: Request, env: Env): Promise<Response> {
  if (!isSecureTransport(request)) {
    return badRequest("Device credentials require HTTPS outside loopback.");
  }
  const allowed = await enforceRateLimit(
    env.DB,
    "register-device",
    clientIpFromRequest(request),
    REGISTER_RATE_LIMIT_PER_HOUR,
  );
  if (!allowed) {
    return tooManyRequests();
  }
  // Global check second, so a caller already over its per-IP cap does not also drain the
  // shared budget.
  if (!(await enforceRateLimit(env.DB, "register-device", GLOBAL_RATE_LIMIT_KEY, GLOBAL_REGISTER_RATE_LIMIT_PER_HOUR))) {
    return tooManyRequests();
  }

  let body: unknown;
  try {
    body = await readJsonBody(request);
  } catch (error) {
    if (error instanceof UnsupportedMediaTypeError) {
      return unsupportedMediaType();
    }
    if (error instanceof JsonBodyTooLargeError) {
      return payloadTooLarge();
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

  const pairingCode = generatePairingCode();
  const pairingCodeHash = await hmacSha256Hex(env.PAIRING_CODE_PEPPER, pairingCode);
  const pairingCodeExpiresUtc = new Date(now.getTime() + PAIRING_CODE_VALIDITY_MS).toISOString();
  // Registration is one logical operation. If either row fails, do not leave an active device
  // whose bearer token was never returned to the caller and therefore cannot be revoked.
  await env.DB.batch([
    insertDeviceStatement(env.DB, {
      id: deviceId,
      publicKeySpki: publicKey,
      desktopTokenHash,
      createdUtc: nowIso,
    }),
    insertPairingCodeStatement(env.DB, {
      codeHash: pairingCodeHash,
      deviceId,
      expiresUtc: pairingCodeExpiresUtc,
    }),
  ]);

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

  // Per-device rotation cap, checked before the body is even read: rotation revokes every
  // sender session and wipes the queued ciphertext, so a compromised-or-looping client must
  // not be able to fire it unboundedly.
  const allowed = await enforceRateLimit(
    env.DB,
    "rotate-key",
    device.id,
    ROTATE_KEY_RATE_LIMIT_PER_HOUR,
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
    if (error instanceof JsonBodyTooLargeError) {
      return payloadTooLarge();
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

  // One atomic batch (see `buildRotateKeyStatements`): new key/token, revoked sender sessions,
  // and the wiped queue together with its status and ownership rows.
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
  // Per-device mint cap, before any minting work: each mint invalidates the previous code, so
  // without a cap a looping client could churn codes (and sessions) without bound.
  const allowed = await enforceRateLimit(
    env.DB,
    "pairing-code-mint",
    device.id,
    PAIRING_CODE_MINT_RATE_LIMIT_PER_HOUR,
  );
  if (!allowed) {
    return tooManyRequests();
  }
  const code = generatePairingCode();
  const codeHash = await hmacSha256Hex(env.PAIRING_CODE_PEPPER, code);
  const expiresUtc = new Date(Date.now() + PAIRING_CODE_VALIDITY_MS).toISOString();
  // Cap live codes at one atomically: concurrent mints must not interleave deletion and insertion
  // and leave multiple simultaneously-valid redeem oracles behind.
  await env.DB.batch([
    deleteUnredeemedPairingCodesStatement(env.DB, device.id),
    insertPairingCodeStatement(env.DB, { codeHash, deviceId: device.id, expiresUtc }),
  ]);
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

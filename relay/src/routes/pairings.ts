import { findActiveSenderSessionByTokenHash, revokeSenderSession } from "../db/devices.js";
import { incrementPairingAttempt, redeemPairingCode } from "../db/pairings.js";
import type { Env } from "../env.js";
import {
  badRequest,
  forbidden,
  gone,
  jsonResponse,
  noContentResponse,
  tooManyRequests,
  unauthorized,
  unsupportedMediaType,
} from "../http/responses.js";
import {
  buildClearedSenderSessionCookie,
  buildSenderSessionCookie,
  readSenderSessionToken,
  SENDER_SESSION_MAX_AGE_SECONDS,
} from "../security/cookies.js";
import { isSameOrigin } from "../security/origin.js";
import { clientIpFromRequest, enforceRateLimit } from "../security/rateLimit.js";
import {
  base64UrlToBytes,
  generateCapabilityToken,
  hmacSha256Hex,
  normalizePairingCodeInput,
  PAIRING_CODE_LENGTH,
  sha256Hex,
  sha256HexOfText,
} from "../security/tokens.js";

const REDEEM_RATE_LIMIT_PER_HOUR = 10;

class UnsupportedMediaTypeError extends Error {}
class MalformedBodyError extends Error {}

async function readJsonBody(request: Request): Promise<unknown> {
  const contentType = request.headers.get("Content-Type") ?? "";
  if (!contentType.toLowerCase().includes("application/json")) {
    throw new UnsupportedMediaTypeError();
  }
  try {
    return await request.json();
  } catch {
    throw new MalformedBodyError();
  }
}

export async function redeemPairing(request: Request, env: Env): Promise<Response> {
  if (!isSameOrigin(request)) {
    return forbidden();
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

  const { code: rawCode } = (body ?? {}) as { code?: unknown };
  if (typeof rawCode !== "string") {
    return badRequest("code is required.");
  }
  const code = normalizePairingCodeInput(rawCode);
  if (code.length !== PAIRING_CODE_LENGTH) {
    return badRequest(`code must be ${PAIRING_CODE_LENGTH} characters.`);
  }

  const allowed = await enforceRateLimit(
    env.DB,
    "redeem-pairing",
    clientIpFromRequest(request),
    REDEEM_RATE_LIMIT_PER_HOUR,
  );
  if (!allowed) {
    return tooManyRequests();
  }

  const now = new Date();
  const nowIso = now.toISOString();
  const codeHash = await hmacSha256Hex(env.PAIRING_CODE_PEPPER, code);

  const attempt = await incrementPairingAttempt(env.DB, codeHash, nowIso);
  if (!attempt || attempt.lockedOut) {
    return gone();
  }

  const sessionId = crypto.randomUUID();
  const sessionToken = generateCapabilityToken();
  const sessionTokenHash = await sha256HexOfText(sessionToken);
  const sessionExpiresUtc = new Date(now.getTime() + SENDER_SESSION_MAX_AGE_SECONDS * 1000).toISOString();

  const redeemed = await redeemPairingCode(env.DB, {
    codeHash,
    nowIso,
    sessionId,
    sessionTokenHash,
    sessionExpiresUtc,
  });
  if (!redeemed) {
    return gone();
  }

  return jsonResponse(
    { publicKey: redeemed.publicKeySpki, deviceId: redeemed.deviceId },
    200,
    { "Set-Cookie": buildSenderSessionCookie(sessionToken) },
  );
}

async function authenticateSenderSession(request: Request, env: Env) {
  const token = readSenderSessionToken(request);
  if (!token) {
    return null;
  }
  const tokenHash = await sha256HexOfText(token);
  return findActiveSenderSessionByTokenHash(env.DB, tokenHash, new Date().toISOString());
}

export async function getSenderDevice(request: Request, env: Env): Promise<Response> {
  const session = await authenticateSenderSession(request, env);
  if (!session) {
    return unauthorized();
  }
  const keyBytes = base64UrlToBytes(session.publicKeySpki);
  const publicKeyFingerprint = keyBytes ? await sha256Hex(keyBytes) : "";
  return jsonResponse({
    publicKey: session.publicKeySpki,
    deviceCreatedUtc: session.deviceCreatedUtc,
    publicKeyFingerprint,
  });
}

export async function disconnectSender(request: Request, env: Env): Promise<Response> {
  if (!isSameOrigin(request)) {
    return forbidden();
  }
  const session = await authenticateSenderSession(request, env);
  if (!session) {
    return unauthorized();
  }
  await revokeSenderSession(env.DB, session.id, new Date().toISOString());
  return noContentResponse({ "Set-Cookie": buildClearedSenderSessionCookie() });
}

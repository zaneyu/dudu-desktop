import { findActiveSenderSessionByTokenHash, revokeSenderSession, type SenderSessionRecord } from "../db/devices.js";
import { incrementPairingAttempt, redeemPairingCode } from "../db/pairings.js";
import type { Env } from "../env.js";
import { JsonBodyTooLargeError, readJsonBody, UnsupportedMediaTypeError } from "../http/body.js";
import {
  badRequest,
  forbidden,
  gone,
  jsonResponse,
  noContentResponse,
  payloadTooLarge,
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
import { isSameOrigin, isSafeCookieGet } from "../security/origin.js";
import {
  clientIpFromRequest,
  enforceRateLimit,
  GLOBAL_RATE_LIMIT_KEY,
  isRateLimitExhausted,
} from "../security/rateLimit.js";
import {
  base64UrlToBytes,
  generateCapabilityToken,
  hmacSha256Hex,
  isPlausibleCapabilityToken,
  normalizePairingCodeInput,
  PAIRING_CODE_CHARSET_PATTERN,
  PAIRING_CODE_LENGTH,
  sha256Hex,
  sha256HexOfText,
} from "../security/tokens.js";

const REDEEM_RATE_LIMIT_PER_HOUR = 10;
/**
 * IP-independent second layer: failed redemptions (unknown, expired, consumed, or locked-out
 * codes) across ALL callers. Once this many fail within the sliding hour, every redemption is
 * refused with 429 until the window slides. The per-code attempt cap only bites once a guess
 * hits a real code, and the per-IP cap is defeated by rotating addresses; this bounds total
 * guessing regardless. Trade-off (acceptable for a single-couple deployment): someone who knows
 * the relay URL can block pairing for about an hour.
 */
const GLOBAL_REDEEM_FAILURE_LIMIT_PER_HOUR = 20;
const REDEEM_FAILURE_SCOPE = "redeem-pairing-failures";

export async function redeemPairing(request: Request, env: Env): Promise<Response> {
  if (!isSameOrigin(request)) {
    return forbidden();
  }

  // Rate limit BEFORE parsing the body: a throttled caller must not cause a body read, let
  // alone reach the code hashing or the pairing-code lookup below.
  const allowed = await enforceRateLimit(
    env.DB,
    "redeem-pairing",
    clientIpFromRequest(request),
    REDEEM_RATE_LIMIT_PER_HOUR,
  );
  if (!allowed) {
    return tooManyRequests();
  }
  if (await isRateLimitExhausted(env.DB, REDEEM_FAILURE_SCOPE, GLOBAL_RATE_LIMIT_KEY, GLOBAL_REDEEM_FAILURE_LIMIT_PER_HOUR)) {
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

  const { code: rawCode } = (body ?? {}) as { code?: unknown };
  if (typeof rawCode !== "string") {
    return badRequest("code is required.");
  }
  const code = normalizePairingCodeInput(rawCode);
  if (code.length !== PAIRING_CODE_LENGTH) {
    return badRequest(`code must be ${PAIRING_CODE_LENGTH} characters.`);
  }
  // Charset gate before hashing: anything outside the Crockford-without-ILOU alphabet can never
  // be a minted code, so reject it as malformed (400) without spending an HMAC or a DB lookup.
  if (!PAIRING_CODE_CHARSET_PATTERN.test(code)) {
    return badRequest("code contains characters outside the pairing-code alphabet.");
  }

  const now = new Date();
  const nowIso = now.toISOString();
  const codeHash = await hmacSha256Hex(env.PAIRING_CODE_PEPPER, code);

  const attempt = await incrementPairingAttempt(env.DB, codeHash, nowIso);
  if (!attempt || attempt.lockedOut) {
    return redeemFailed(env);
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
    return redeemFailed(env);
  }

  return jsonResponse(
    {
      publicKey: redeemed.publicKeySpki,
      publicKeyFingerprint: await fingerprintStoredPublicKey(redeemed.publicKeySpki),
      deviceId: redeemed.deviceId,
    },
    200,
    { "Set-Cookie": buildSenderSessionCookie(sessionToken) },
  );
}

/** Counts one failed redemption against the global failure budget, then answers 410. */
async function redeemFailed(env: Env): Promise<Response> {
  await enforceRateLimit(env.DB, REDEEM_FAILURE_SCOPE, GLOBAL_RATE_LIMIT_KEY, GLOBAL_REDEEM_FAILURE_LIMIT_PER_HOUR);
  return gone();
}

async function fingerprintStoredPublicKey(publicKeySpki: string): Promise<string> {
  const keyBytes = base64UrlToBytes(publicKeySpki);
  if (!keyBytes) {
    throw new Error("Stored device public key is not valid Base64URL.");
  }
  return sha256Hex(keyBytes);
}

/** Also used by `routes/messages.ts`: every sender-authenticated message route shares this same
 * cookie-based session lookup. */
export async function authenticateSenderSession(
  request: Request,
  env: Env,
): Promise<SenderSessionRecord | null> {
  const token = readSenderSessionToken(request);
  if (!token || !isPlausibleCapabilityToken(token)) {
    return null;
  }
  const tokenHash = await sha256HexOfText(token);
  return findActiveSenderSessionByTokenHash(env.DB, tokenHash, new Date().toISOString());
}

export async function getSenderDevice(request: Request, env: Env): Promise<Response> {
  // Read-only cookie GET: assert Origin/Referer when present (see `isSafeCookieGet`).
  if (!isSafeCookieGet(request)) {
    return forbidden();
  }
  const session = await authenticateSenderSession(request, env);
  if (!session) {
    return unauthorized();
  }
  const keyBytes = base64UrlToBytes(session.publicKeySpki);
  if (!keyBytes) {
    // The public key was validated as SPKI Base64URL at registration time, so a stored row that
    // fails to decode here indicates stored data corruption, not a client error.
    throw new Error("Stored device public key is not valid Base64URL.");
  }
  const publicKeyFingerprint = await sha256Hex(keyBytes);
  return jsonResponse({
    publicKey: session.publicKeySpki,
    deviceId: session.deviceId,
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

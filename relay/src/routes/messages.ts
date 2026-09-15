/**
 * The encrypted message queue: `POST /v1/messages` (sender, cookie-authenticated), `GET
 * /v1/messages` and `POST /v1/messages/:id/ack` (desktop, bearer-authenticated), and `GET
 * /v1/messages/:id/status` (sender, cookie-authenticated). The relay stores and returns
 * ciphertext plus routing metadata only; nothing here accepts or reports `opened`/`read`/`seen`.
 */
import { authenticateDevice } from "./devices.js";
import { authenticateSenderSession } from "./pairings.js";
import {
  ackMessage as ackMessageRow,
  findMessageStateForSession,
  findMessageStatusOwner,
  insertQueuedMessage,
  claimEligibleMessages,
  findMessageEnvelope,
  messageIdExists,
} from "../db/messages.js";
import type { Env } from "../env.js";
import { JsonBodyTooLargeError, readJsonBody, UnsupportedMediaTypeError } from "../http/body.js";
import {
  badRequest,
  conflict,
  forbidden,
  jsonResponse,
  noContentResponse,
  notFound,
  payloadTooLarge,
  tooManyRequests,
  unauthorized,
  unprocessable,
  unsupportedMediaType,
} from "../http/responses.js";
import { MESSAGE_ID_PATTERN, MESSAGE_RETENTION_DAYS, type PostMessageResponse } from "../protocol/types.js";
import {
  EnvelopeMalformedError,
  EnvelopeSemanticError,
  EnvelopeTooLargeError,
  validateIncomingEnvelope,
} from "../security/envelopeValidation.js";
import { isSameOrigin, isSafeCookieGet } from "../security/origin.js";
import { enforceRateLimit } from "../security/rateLimit.js";
import { envelopeFingerprint } from "../security/envelopeFingerprint.js";

const SUBMIT_RATE_LIMIT_PER_HOUR = 60;
/**
 * Per-device hourly cap on `GET /v1/messages` polls, keyed by device id: polling is the only
 * desktop route that returns queue contents, so a tight loop (compromised client or bug) must
 * not be able to spin the claim machinery unboundedly. The desktop polls every 12-18s (about
 * 240/hour) plus backoff retries, so 600/hour (one poll per 6s) leaves headroom for restarts
 * while still biting a hot loop within minutes.
 */
const POLL_RATE_LIMIT_PER_HOUR = 600;
const STATUS_GRACE_MS = 24 * 60 * 60 * 1000;
const RETENTION_MS = MESSAGE_RETENTION_DAYS * 24 * 60 * 60 * 1000;

/**
 * Shape check for `:id` path parameters: only a canonical lowercase UUID can name a message.
 * Anything else is answered 404 (not 400) so malformed ids are indistinguishable from unknown
 * ones — a desktop must never be able to probe which id shapes reach the DB layer.
 */
function isValidMessageId(id: string): boolean {
  return MESSAGE_ID_PATTERN.test(id);
}

export async function postMessage(request: Request, env: Env): Promise<Response> {
  if (!isSameOrigin(request)) {
    return forbidden();
  }
  const session = await authenticateSenderSession(request, env);
  if (!session) {
    return unauthorized();
  }

  // Scoped per sender session (not per IP): the cap is "sixty per sender per hour", and a sender
  // session is exactly the unit that identity applies to. Bucket key is SHA-256("messages:<session
  // id>"), per the controller's explicit ruling on the scope string.
  const allowed = await enforceRateLimit(env.DB, "messages", session.id, SUBMIT_RATE_LIMIT_PER_HOUR);
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

  const validationNow = new Date();
  let envelope;
  try {
    envelope = await validateIncomingEnvelope(body, validationNow);
  } catch (error) {
    if (error instanceof EnvelopeTooLargeError) {
      return payloadTooLarge();
    }
    if (error instanceof EnvelopeSemanticError) {
      return unprocessable(error.message);
    }
    if (error instanceof EnvelopeMalformedError) {
      return badRequest();
    }
    throw error;
  }

  // Use a fresh timestamp for write-side expiry calculations and the active-session guard;
  // validation can involve a non-trivial key import, and a session must not remain writable just
  // because it was valid when JSON validation began.
  const now = new Date();
  const nowIso = now.toISOString();
  const envelopeHash = await envelopeFingerprint(envelope);
  const existingOwner = await findMessageStatusOwner(env.DB, envelope.messageId);
  if (existingOwner) {
    const ownerHash =
      existingOwner.envelopeHash ??
      (await findMessageEnvelope(env.DB, envelope.messageId, existingOwner.deviceId)
        .then((legacyEnvelope) => (legacyEnvelope ? envelopeFingerprint(legacyEnvelope) : null)));
    if (
      existingOwner.senderSessionId !== session.id ||
      ownerHash === null ||
      ownerHash !== envelopeHash
    ) {
      return conflict();
    }
    const response: PostMessageResponse = { messageId: envelope.messageId, status: existingOwner.state };
    return jsonResponse(response, 200);
  }

  const createdMs = Date.parse(envelope.createdUtc);
  const deliverAfterMs = envelope.deliverAfterUtc ? Date.parse(envelope.deliverAfterUtc) : createdMs;
  const expiresUtc = new Date(Math.max(createdMs, deliverAfterMs) + RETENTION_MS).toISOString();
  // While queued, the sender's status lives exactly as long as the ciphertext it describes: a
  // desktop that is offline for days (or a message scheduled weeks out) must still read as
  // "queued", not vanish. ackMessage shortens it to a 24-hour grace after actual delivery.
  const statusExpiresUtc = expiresUtc;

  const inserted = await insertQueuedMessage(env.DB, {
    id: envelope.messageId,
    deviceId: session.deviceId,
    nowIso,
    protocolVersion: envelope.protocolVersion,
    createdUtc: envelope.createdUtc,
    deliverAfterUtc: envelope.deliverAfterUtc,
    expiresUtc,
    ephemeralPublicKey: envelope.ephemeralPublicKey,
    hkdfSalt: envelope.hkdfSalt,
    nonce: envelope.nonce,
    ciphertext: envelope.ciphertext,
    senderSessionId: session.id,
    envelopeHash,
    statusExpiresUtc,
  });
  if (!inserted) {
    // A concurrent request won the globally-unique message id. The pre-insert lookup above can
    // legitimately miss that winner, so resolve ownership again after the atomic insert instead
    // of reporting success for a ciphertext this request did not queue.
    const ownerAfterRace = await findMessageStatusOwner(env.DB, envelope.messageId);
    if (!ownerAfterRace) {
      // A pre-0004 row may have a ciphertext but no recoverable ownership record. Refuse to reuse
      // that id rather than risk replacing or reporting success for legacy ciphertext.
      if (await messageIdExists(env.DB, envelope.messageId)) {
        return conflict();
      }
      // The authenticated session/device may have been revoked by a concurrent delete or key
      // rotation between the initial auth lookup and this atomic insert. No message was queued.
      return unauthorized();
    }
    const ownerHash =
      ownerAfterRace.envelopeHash ??
      (await findMessageEnvelope(env.DB, envelope.messageId, ownerAfterRace.deviceId)
        .then((legacyEnvelope) => (legacyEnvelope ? envelopeFingerprint(legacyEnvelope) : null)));
    if (
      ownerAfterRace.senderSessionId !== session.id ||
      ownerHash === null ||
      ownerHash !== envelopeHash
    ) {
      return conflict();
    }
    return jsonResponse({ messageId: envelope.messageId, status: ownerAfterRace.state }, 200);
  }

  const response: PostMessageResponse = { messageId: envelope.messageId, status: "queued" };
  return jsonResponse(response, 202);
}

export async function getMessages(request: Request, env: Env): Promise<Response> {
  const device = await authenticateDevice(request, env);
  if (!device) {
    return unauthorized();
  }
  const allowed = await enforceRateLimit(env.DB, "poll-messages", device.id, POLL_RATE_LIMIT_PER_HOUR);
  if (!allowed) {
    return tooManyRequests();
  }
  const messages = await claimEligibleMessages(env.DB, device.id, new Date());
  return jsonResponse({ messages });
}

export async function ackMessage(
  request: Request,
  env: Env,
  _ctx: ExecutionContext,
  params: Record<string, string>,
): Promise<Response> {
  const device = await authenticateDevice(request, env);
  if (!device) {
    return unauthorized();
  }
  if (!isValidMessageId(params.id)) {
    return notFound();
  }
  const now = new Date();
  const statusExpiresUtc = new Date(now.getTime() + STATUS_GRACE_MS).toISOString();
  const result = await ackMessageRow(env.DB, params.id, device.id, now.toISOString(), statusExpiresUtc);
  if (result === "not_found") {
    return notFound();
  }
  return noContentResponse();
}

export async function getMessageStatus(
  request: Request,
  env: Env,
  _ctx: ExecutionContext,
  params: Record<string, string>,
): Promise<Response> {
  // Read-only cookie GET: assert Origin/Referer when present (see `isSafeCookieGet`).
  if (!isSafeCookieGet(request)) {
    return forbidden();
  }
  const session = await authenticateSenderSession(request, env);
  if (!session) {
    return unauthorized();
  }
  if (!isValidMessageId(params.id)) {
    return notFound();
  }
  const state = await findMessageStateForSession(env.DB, params.id, session.id, new Date().toISOString());
  if (!state) {
    return notFound();
  }
  return jsonResponse({ status: state });
}

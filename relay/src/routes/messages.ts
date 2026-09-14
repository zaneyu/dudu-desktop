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
  listEligibleMessages,
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
import { MESSAGE_RETENTION_DAYS, type PostMessageResponse } from "../protocol/types.js";
import {
  EnvelopeMalformedError,
  EnvelopeSemanticError,
  EnvelopeTooLargeError,
  validateIncomingEnvelope,
} from "../security/envelopeValidation.js";
import { isSameOrigin } from "../security/origin.js";
import { enforceRateLimit } from "../security/rateLimit.js";

const SUBMIT_RATE_LIMIT_PER_HOUR = 60;
const STATUS_GRACE_MS = 24 * 60 * 60 * 1000;
const RETENTION_MS = MESSAGE_RETENTION_DAYS * 24 * 60 * 60 * 1000;

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

  const now = new Date();
  let envelope;
  try {
    envelope = await validateIncomingEnvelope(body, now);
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

  const existingOwner = await findMessageStatusOwner(env.DB, envelope.messageId);
  if (existingOwner) {
    if (existingOwner.senderSessionId !== session.id) {
      return conflict();
    }
    const response: PostMessageResponse = { messageId: envelope.messageId, status: existingOwner.state };
    return jsonResponse(response, 200);
  }

  const createdMs = Date.parse(envelope.createdUtc);
  const deliverAfterMs = envelope.deliverAfterUtc ? Date.parse(envelope.deliverAfterUtc) : createdMs;
  const expiresUtc = new Date(Math.max(createdMs, deliverAfterMs) + RETENTION_MS).toISOString();
  // message_status is short-lived relative to delivery, not enqueue time: a scheduled message
  // can sit in the queue for up to 30 days, so its sender must still be able to see queued status
  // when delivery becomes eligible. ackMessage uses the same 24-hour grace after actual delivery.
  const statusExpiresUtc = new Date(Math.max(createdMs, deliverAfterMs) + STATUS_GRACE_MS).toISOString();

  await insertQueuedMessage(env.DB, {
    id: envelope.messageId,
    deviceId: session.deviceId,
    protocolVersion: envelope.protocolVersion,
    createdUtc: envelope.createdUtc,
    deliverAfterUtc: envelope.deliverAfterUtc,
    expiresUtc,
    ephemeralPublicKey: envelope.ephemeralPublicKey,
    hkdfSalt: envelope.hkdfSalt,
    nonce: envelope.nonce,
    ciphertext: envelope.ciphertext,
    senderSessionId: session.id,
    statusExpiresUtc,
  });

  const response: PostMessageResponse = { messageId: envelope.messageId, status: "queued" };
  return jsonResponse(response, 202);
}

export async function getMessages(request: Request, env: Env): Promise<Response> {
  const device = await authenticateDevice(request, env);
  if (!device) {
    return unauthorized();
  }
  const messages = await listEligibleMessages(env.DB, device.id, new Date().toISOString());
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
  const session = await authenticateSenderSession(request, env);
  if (!session) {
    return unauthorized();
  }
  const state = await findMessageStateForSession(env.DB, params.id, session.id, new Date().toISOString());
  if (!state) {
    return notFound();
  }
  return jsonResponse({ status: state });
}

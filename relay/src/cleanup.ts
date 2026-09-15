/**
 * Hourly scheduled cleanup, invoked once from the Worker's `scheduled` handler. Every deletion
 * here is a pure retention/hygiene sweep — nothing here is on any request's critical path, and
 * nothing here logs request data.
 */
import { deleteStaleSenderSessions } from "./db/devices.js";
import {
  deleteExpiredMessageOwnership,
  deleteExpiredMessages,
  deleteExpiredMessageStatuses,
} from "./db/messages.js";
import { deleteExpiredPairingCodes } from "./db/pairings.js";
import type { Env } from "./env.js";
import { deleteStaleRateLimitBuckets, hourWindowStartUtc } from "./security/rateLimit.js";

/** Sender sessions are kept this long past revocation/expiry, so a just-disconnected sender still
 * has a short grace window for any in-flight request that read the cookie before revocation. */
const SESSION_CLEANUP_GRACE_MS = 24 * 60 * 60 * 1000;

export async function cleanupExpired(env: Env, now: Date): Promise<void> {
  const nowIso = now.toISOString();
  const sessionCutoffIso = new Date(now.getTime() - SESSION_CLEANUP_GRACE_MS).toISOString();
  const currentWindowStartIso = hourWindowStartUtc(now);

  await deleteExpiredMessages(env.DB, nowIso);
  await deleteExpiredMessageOwnership(env.DB, nowIso);
  await deleteExpiredMessageStatuses(env.DB, nowIso);
  await deleteExpiredPairingCodes(env.DB, nowIso);
  await deleteStaleSenderSessions(env.DB, sessionCutoffIso);
  await deleteStaleRateLimitBuckets(env.DB, currentWindowStartIso);
}

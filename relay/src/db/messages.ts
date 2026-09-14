/**
 * Persistence for the `messages` (queued ciphertext) and `message_status` (delivery state)
 * tables. `message_status.id` — a global, non-composite primary key — is the single source of
 * truth for "which sender session owns this message id": `messages` alone cannot answer that
 * once a row has been deleted (on ack, on rotation, on expiry), but `message_status` persists
 * past ciphertext deletion specifically so ownership and delivery state stay answerable.
 */
import type { EncryptedEnvelopeV1 } from "../protocol/types.js";

export interface QueuedMessageParams {
  id: string;
  deviceId: string;
  nowIso: string;
  protocolVersion: number;
  createdUtc: string;
  deliverAfterUtc: string | null;
  expiresUtc: string;
  ephemeralPublicKey: string;
  hkdfSalt: string;
  nonce: string;
  ciphertext: string;
  senderSessionId: string;
  /** `message_status.expires_utc` at insert time — `createdUtc + 24h`, independent of
   * `expiresUtc` above (the `messages` row's 30-day ciphertext retention). Status rows are
   * meant to be short-lived regardless of how long the ciphertext itself is retained; `ackMessage`
   * later re-sets this to `now + 24h` on delivery, using the same 24h constant. */
  statusExpiresUtc: string;
}

/**
 * Inserts the status row before the ciphertext row, atomically. The ciphertext insert is
 * conditional on the status row belonging to this sender/device. This ordering matters because
 * `message_status.id` is globally unique while `messages` has a per-device key: two devices
 * racing to submit the same id must not leave an orphan ciphertext owned by the losing device.
 * Returns true only when this call created the status row; callers use false to resolve a race as
 * idempotent success for the same sender or 409 for a different sender.
 */
export async function insertQueuedMessage(db: D1Database, params: QueuedMessageParams): Promise<boolean> {
  const results = await db.batch([
    db
      .prepare(
        `INSERT INTO message_status (id, sender_session_id, device_id, state, updated_utc, expires_utc)
         SELECT ?1, ?2, ?3, 'queued', ?4, ?5
         FROM sender_sessions s
         JOIN devices d ON d.id = s.device_id
         WHERE s.id = ?6
           AND s.device_id = ?7
           AND s.revoked_utc IS NULL
           AND s.expires_utc > ?8
           AND d.revoked_utc IS NULL
         ON CONFLICT (id) DO NOTHING`,
      )
      .bind(
        params.id,
        params.senderSessionId,
        params.deviceId,
        params.createdUtc,
        params.statusExpiresUtc,
        params.senderSessionId,
        params.deviceId,
        params.nowIso,
      ),
    db
      .prepare(
        `INSERT INTO messages
           (id, device_id, protocol_version, created_utc, deliver_after_utc, expires_utc,
            ephemeral_public_key, hkdf_salt, nonce, ciphertext)
         SELECT ?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10
         WHERE EXISTS (
           SELECT 1 FROM message_status
           WHERE id = ?11 AND sender_session_id = ?12 AND device_id = ?13
         )
         ON CONFLICT (device_id, id) DO NOTHING`,
      )
      .bind(
        params.id,
        params.deviceId,
        params.protocolVersion,
        params.createdUtc,
        params.deliverAfterUtc,
        params.expiresUtc,
        params.ephemeralPublicKey,
        params.hkdfSalt,
        params.nonce,
        params.ciphertext,
        params.id,
        params.senderSessionId,
        params.deviceId,
      ),
  ]);
  return (results[0]?.meta.changes ?? 0) > 0;
}

export interface MessageStatusOwner {
  senderSessionId: string;
  deviceId: string;
  state: "queued" | "delivered";
}

/** Looks up who owns a message id, for the idempotent-resubmit (same session) vs. 409 (different
 * session) decision in `POST /v1/messages`. */
export async function findMessageStatusOwner(db: D1Database, id: string): Promise<MessageStatusOwner | null> {
  const row = await db
    .prepare(`SELECT sender_session_id, device_id, state FROM message_status WHERE id = ?1`)
    .bind(id)
    .first<{ sender_session_id: string; device_id: string; state: "queued" | "delivered" }>();
  if (!row) {
    return null;
  }
  return { senderSessionId: row.sender_session_id, deviceId: row.device_id, state: row.state };
}

export type MessageState = "queued" | "delivered" | "expired";

/**
 * Resolves the status a sender should see for one of its own messages: `delivered` once acked;
 * otherwise `queued` while the ciphertext row still exists and has not passed its own expiry, and
 * `expired` once it is gone or stale — whether cleaned up already or merely overdue for cleanup.
 * Returns `null` when no status row is owned by this session (unknown id, or a different
 * session's message) — callers map that to 404.
 */
export async function findMessageStateForSession(
  db: D1Database,
  id: string,
  senderSessionId: string,
  nowIso: string,
): Promise<MessageState | null> {
  const statusRow = await db
    .prepare(`SELECT device_id, state FROM message_status WHERE id = ?1 AND sender_session_id = ?2`)
    .bind(id, senderSessionId)
    .first<{ device_id: string; state: "queued" | "delivered" }>();
  if (!statusRow) {
    return null;
  }
  if (statusRow.state === "delivered") {
    return "delivered";
  }
  const messageRow = await db
    .prepare(`SELECT expires_utc FROM messages WHERE device_id = ?1 AND id = ?2`)
    .bind(statusRow.device_id, id)
    .first<{ expires_utc: string }>();
  if (!messageRow || messageRow.expires_utc <= nowIso) {
    return "expired";
  }
  return "queued";
}

interface MessageRow {
  id: string;
  protocol_version: number;
  created_utc: string;
  deliver_after_utc: string | null;
  ephemeral_public_key: string;
  hkdf_salt: string;
  nonce: string;
  ciphertext: string;
}

function toEnvelope(row: MessageRow): EncryptedEnvelopeV1 {
  return {
    protocolVersion: row.protocol_version as 1,
    messageId: row.id,
    createdUtc: row.created_utc,
    deliverAfterUtc: row.deliver_after_utc,
    ephemeralPublicKey: row.ephemeral_public_key,
    hkdfSalt: row.hkdf_salt,
    nonce: row.nonce,
    ciphertext: row.ciphertext,
  };
}

const MAXIMUM_ELIGIBLE_MESSAGES = 20;

/**
 * The serialized-byte budget for one poll page — 48 KiB, comfortably inside the desktop's 64 KiB
 * hard response cap (review C1: twenty maximum-size envelopes are ~168 KiB, which the desktop
 * refuses to read, and an unread page is never acked, so the queue stalled there forever). The
 * row count above stays as a secondary cap for the ordinary small-envelope case.
 */
export const MAXIMUM_PAGE_BYTES = 49_152;

/**
 * The oldest eligible (not scheduled in the future) envelopes for one device, in delivery then
 * creation order — byte-for-byte what was submitted, never re-derived. The page stops at
 * whichever comes first: {@link MAXIMUM_ELIGIBLE_MESSAGES} rows, or the next envelope not fitting
 * in {@link MAXIMUM_PAGE_BYTES}. The first eligible envelope is always returned even if it alone
 * exceeds the budget (at the 6144-byte ciphertext maximum a single envelope is ~8.6 KB, so this
 * never actually happens today) — a page that can starve is worse than a page that is slightly
 * over budget. Whatever does not fit is returned by the next poll, once these are acked.
 */
export async function listEligibleMessages(
  db: D1Database,
  deviceId: string,
  nowIso: string,
): Promise<EncryptedEnvelopeV1[]> {
  const { results } = await db
    .prepare(
      `SELECT id, protocol_version, created_utc, deliver_after_utc, ephemeral_public_key, hkdf_salt,
              nonce, ciphertext
       FROM messages
       WHERE device_id = ?1
         AND (deliver_after_utc IS NULL OR deliver_after_utc <= ?2)
         AND expires_utc > ?2
       ORDER BY COALESCE(deliver_after_utc, created_utc) ASC, created_utc ASC
       LIMIT ?3`,
    )
    .bind(deviceId, nowIso, MAXIMUM_ELIGIBLE_MESSAGES)
    .all<MessageRow>();

  // Measured against the same JSON the route actually sends -- `{"messages":[...]}` -- so the
  // budget covers the wrapper and the separators, not just the envelopes themselves.
  const encoder = new TextEncoder();
  const page: EncryptedEnvelopeV1[] = [];
  let pageBytes = encoder.encode(`{"messages":[]}`).length;
  for (const row of results) {
    const envelope = toEnvelope(row);
    const envelopeBytes = encoder.encode(JSON.stringify(envelope)).length + (page.length > 0 ? 1 : 0);
    if (page.length > 0 && pageBytes + envelopeBytes > MAXIMUM_PAGE_BYTES) {
      break;
    }
    page.push(envelope);
    pageBytes += envelopeBytes;
  }
  return page;
}

export type AckResult = "not_found" | "ok";

/**
 * Acknowledges delivery: deletes the ciphertext and marks the status row delivered, atomically.
 * Idempotent — re-acking an already-delivered message is a no-op `"ok"`, since the status row
 * (unlike the ciphertext) persists past the first ack. `"not_found"` covers both an unknown id
 * and an id owned by a different device, so a desktop can never probe another device's ids.
 */
export async function ackMessage(
  db: D1Database,
  id: string,
  deviceId: string,
  nowIso: string,
  statusExpiresUtc: string,
): Promise<AckResult> {
  const statusRow = await db
    .prepare(`SELECT device_id, state FROM message_status WHERE id = ?1`)
    .bind(id)
    .first<{ device_id: string; state: "queued" | "delivered" }>();
  if (!statusRow || statusRow.device_id !== deviceId) {
    return "not_found";
  }
  if (statusRow.state === "delivered") {
    return "ok";
  }
  await db.batch([
    db.prepare(`DELETE FROM messages WHERE device_id = ?1 AND id = ?2`).bind(deviceId, id),
    db
      .prepare(`UPDATE message_status SET state = 'delivered', updated_utc = ?1, expires_utc = ?2 WHERE id = ?3`)
      .bind(nowIso, statusExpiresUtc, id),
  ]);
  return "ok";
}

/** A `DELETE FROM messages` statement for one device, for callers (key rotation, device deletion)
 * that must include it in their own atomic `db.batch`. */
export function deleteMessagesStatement(db: D1Database, deviceId: string): D1PreparedStatement {
  return db.prepare(`DELETE FROM messages WHERE device_id = ?1`).bind(deviceId);
}

/** A `DELETE FROM message_status` statement for one device, for device deletion's batch. Key
 * rotation deliberately does NOT include this: a rotated device's orphaned status rows still
 * resolve correctly via `findMessageStateForSession` (no `messages` row -> `"expired"`). */
export function deleteMessageStatusesStatement(db: D1Database, deviceId: string): D1PreparedStatement {
  return db.prepare(`DELETE FROM message_status WHERE device_id = ?1`).bind(deviceId);
}

/** Scheduled cleanup: ciphertext rows past their retention expiry, regardless of delivery state. */
export async function deleteExpiredMessages(db: D1Database, nowIso: string): Promise<void> {
  await db.prepare(`DELETE FROM messages WHERE expires_utc < ?1`).bind(nowIso).run();
}

/** Scheduled cleanup: status rows past their own expiry — 24h after creation while still queued
 * (set by `insertQueuedMessage`), or 24h after delivery once `ackMessage` re-sets it. */
export async function deleteExpiredMessageStatuses(db: D1Database, nowIso: string): Promise<void> {
  await db.prepare(`DELETE FROM message_status WHERE expires_utc < ?1`).bind(nowIso).run();
}

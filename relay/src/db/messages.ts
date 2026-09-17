/**
 * Persistence for the `messages` (queued ciphertext) and `message_status` (delivery state)
 * tables. `message_ownership.id` — a global, non-composite primary key — is the single source of
 * truth for "which sender session owns this message id": the short-lived `message_status` row is
 * intentionally allowed to expire independently, while ownership persists through ciphertext
 * retention so retries cannot replace or masquerade as an older envelope.
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
  envelopeHash: string;
  expectedPublicKeySpki: string;
  /** `message_status.expires_utc` at insert time — the same as `expiresUtc` (ciphertext
   * retention), so a queued status never expires before the message it describes. `ackMessage`
   * later re-sets this to `now + 24h` on delivery. */
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
        `INSERT INTO message_ownership
           (id, sender_session_id, device_id, envelope_hash, state, created_utc, expires_utc)
         SELECT ?1, ?2, ?3, ?4, 'queued', ?5, ?6
         FROM sender_sessions s
         JOIN devices d ON d.id = s.device_id
         WHERE s.id = ?7
           AND s.device_id = ?8
           AND s.revoked_utc IS NULL
           AND s.expires_utc > ?9
           AND d.revoked_utc IS NULL AND d.public_key_spki = ?10
           AND NOT EXISTS (SELECT 1 FROM messages m WHERE m.id = ?1)
         ON CONFLICT (id) DO NOTHING`,
      )
      .bind(
        params.id,
        params.senderSessionId,
        params.deviceId,
        params.envelopeHash,
        params.createdUtc,
        params.expiresUtc,
        params.senderSessionId,
        params.deviceId,
        params.nowIso,
        params.expectedPublicKeySpki,
      ),
    db
      .prepare(
        `INSERT INTO message_status (id, sender_session_id, device_id, state, updated_utc, expires_utc)
         SELECT ?1, ?2, ?3, 'queued', ?4, ?5
         FROM message_ownership
         WHERE id = ?6 AND changes() = 1 AND state = 'queued'
           AND sender_session_id = ?7
           AND device_id = ?8
           AND envelope_hash = ?9
         ON CONFLICT (id) DO NOTHING`,
      )
      .bind(
        params.id,
        params.senderSessionId,
        params.deviceId,
        params.createdUtc,
        params.statusExpiresUtc,
        params.id,
        params.senderSessionId,
        params.deviceId,
        params.envelopeHash,
      ),
    db
      .prepare(
        `INSERT INTO messages
           (id, device_id, protocol_version, created_utc, deliver_after_utc, expires_utc,
            ephemeral_public_key, hkdf_salt, nonce, ciphertext)
         SELECT ?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10
         WHERE changes() = 1 AND EXISTS (
           SELECT 1 FROM message_ownership
           WHERE id = ?11 AND sender_session_id = ?12 AND device_id = ?13 AND envelope_hash = ?14
             AND state = 'queued'
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
        params.envelopeHash,
      ),
  ]);
  return (results[0]?.meta.changes ?? 0) > 0;
}

export interface MessageStatusOwner {
  senderSessionId: string;
  deviceId: string;
  state: "queued" | "delivered";
  envelopeHash: string | null;
}

/** Looks up who owns a message id, for the idempotent-resubmit (same session) vs. 409 (different
 * session) decision in `POST /v1/messages`. */
export async function findMessageStatusOwner(db: D1Database, id: string): Promise<MessageStatusOwner | null> {
  const row = await db
    .prepare(`SELECT sender_session_id, device_id, state, envelope_hash FROM message_ownership WHERE id = ?1`)
    .bind(id)
    .first<{
      sender_session_id: string;
      device_id: string;
      state: "queued" | "delivered";
      envelope_hash: string | null;
    }>();
  if (!row) {
    return null;
  }
  return {
    senderSessionId: row.sender_session_id,
    deviceId: row.device_id,
    state: row.state,
    envelopeHash: row.envelope_hash,
  };
}

/** True when a legacy/pre-migration ciphertext still occupies an id without an ownership row. */
export async function messageIdExists(db: D1Database, id: string): Promise<boolean> {
  const row = await db.prepare(`SELECT 1 FROM messages WHERE id = ?1 LIMIT 1`).bind(id).first();
  return row != null;
}

export type MessageState = "queued" | "delivered" | "expired";

/**
 * Resolves the status a sender should see for one of its own messages: `delivered` once acked;
 * otherwise `queued` while the ciphertext row still exists and has not passed its own expiry, and
 * `expired` once it is gone or stale — whether cleaned up already or merely overdue for cleanup.
 *
 * When the `message_status` row is already gone (swept 24h after delivery, or a legacy row with
 * a short expiry) but the longer-lived `message_ownership` row still names this session as the
 * owner, the id resolves from ownership plus the ciphertext row instead of 404: a sender that
 * still holds a valid ownership record must never be told its message never existed. Returns `null` — callers map that to 404 — only when no ownership record names this
 * session (unknown id, or a different session's message).
 */
export async function findMessageStateForSession(
  db: D1Database,
  id: string,
  senderSessionId: string,
  nowIso: string,
): Promise<MessageState | null> {
  const row = await db.prepare(`
    SELECT CASE WHEN state = 'delivered' THEN 'delivered'
      WHEN EXISTS (SELECT 1 FROM messages m WHERE m.id = ?1 AND m.device_id = owner.device_id
        AND m.expires_utc > ?3) THEN 'queued' ELSE 'expired' END AS state
    FROM (
      SELECT device_id, state FROM message_ownership WHERE id = ?1 AND sender_session_id = ?2
      UNION ALL
      SELECT device_id, state FROM message_status WHERE id = ?1 AND sender_session_id = ?2
    ) owner LIMIT 1
  `).bind(id, senderSessionId, nowIso).first<{ state: MessageState }>();
  return row?.state ?? null;
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
  delivery_claim_token?: string | null;
  delivery_claim_expires_utc?: string | null;
}

/** Resolves a complete legacy envelope when migration could preserve ownership but not its hash. */
export async function findMessageEnvelope(
  db: D1Database,
  id: string,
  deviceId: string,
): Promise<EncryptedEnvelopeV1 | null> {
  const row = await db
    .prepare(
      `SELECT id, protocol_version, created_utc, deliver_after_utc, ephemeral_public_key, hkdf_salt,
              nonce, ciphertext
       FROM messages WHERE id = ?1 AND device_id = ?2`,
    )
    .bind(id, deviceId)
    .first<MessageRow>();
  return row ? toEnvelope(row) : null;
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
         AND (deliver_after_utc IS NULL OR julianday(deliver_after_utc) <= julianday(?2))
         AND expires_utc > ?2
       ORDER BY CASE WHEN deliver_after_utc IS NULL THEN julianday(created_utc)
                    ELSE julianday(deliver_after_utc) END ASC,
                julianday(created_utc) ASC
       LIMIT ?3`,
    )
    .bind(deviceId, nowIso, MAXIMUM_ELIGIBLE_MESSAGES)
    .all<MessageRow>();

  return pageFromRows(results);
}

const DELIVERY_CLAIM_MS = 2 * 60 * 1000;

/**
 * Claims the page before returning it. D1 batches execute sequentially and atomically, so two
 * concurrent polls may select the same candidates but only one can update their unexpired claim
 * fields; the losing poll re-reads by its own claim token and returns an empty page.
 */
export async function claimEligibleMessages(
  db: D1Database,
  deviceId: string,
  now: Date,
): Promise<EncryptedEnvelopeV1[]> {
  // A poll that loses the claim race to a concurrent poll would otherwise return an empty page
  // even when other unclaimed messages are eligible; re-select once before giving up.
  const first = await claimEligibleMessagesOnce(db, deviceId, now);
  if (first.claimed.length > 0 || !first.hadCandidates) {
    return first.claimed;
  }
  return (await claimEligibleMessagesOnce(db, deviceId, now)).claimed;
}

async function claimEligibleMessagesOnce(
  db: D1Database,
  deviceId: string,
  now: Date,
): Promise<{ claimed: EncryptedEnvelopeV1[]; hadCandidates: boolean }> {
  const nowIso = now.toISOString();
  const claimExpiresUtc = new Date(now.getTime() + DELIVERY_CLAIM_MS).toISOString();
  const claimToken = crypto.randomUUID();
  const { results } = await db
    .prepare(
      `SELECT id, protocol_version, created_utc, deliver_after_utc, ephemeral_public_key, hkdf_salt,
              nonce, ciphertext
       FROM messages
       WHERE device_id = ?1
         AND (deliver_after_utc IS NULL OR julianday(deliver_after_utc) <= julianday(?2))
         AND expires_utc > ?2
         AND (delivery_claim_expires_utc IS NULL OR delivery_claim_expires_utc <= ?2)
       ORDER BY CASE WHEN deliver_after_utc IS NULL THEN julianday(created_utc)
                    ELSE julianday(deliver_after_utc) END ASC,
                julianday(created_utc) ASC
       LIMIT ?3`,
    )
    .bind(deviceId, nowIso, MAXIMUM_ELIGIBLE_MESSAGES)
    .all<MessageRow>();

  const candidatePage = pageFromRows(results);
  if (candidatePage.length === 0) {
    return { claimed: [], hadCandidates: false };
  }

  const idPlaceholders = candidatePage.map((_row, index) => `?${index + 5}`).join(", ");
  const updateBindings: unknown[] = [claimToken, claimExpiresUtc, deviceId, nowIso];
  updateBindings.push(...candidatePage.map((envelope) => envelope.messageId));
  const update = db.prepare(
    `UPDATE messages
     SET delivery_claim_token = ?1, delivery_claim_expires_utc = ?2
     WHERE device_id = ?3
       AND expires_utc > ?4
       AND (deliver_after_utc IS NULL OR julianday(deliver_after_utc) <= julianday(?4))
       AND (delivery_claim_expires_utc IS NULL OR delivery_claim_expires_utc <= ?4)
       AND id IN (${idPlaceholders})`,
  ).bind(...updateBindings);
  const selected = await db.batch([
    update,
    db
      .prepare(
        `SELECT id, protocol_version, created_utc, deliver_after_utc, ephemeral_public_key, hkdf_salt,
                nonce, ciphertext
         FROM messages
         WHERE device_id = ?1 AND delivery_claim_token = ?2
         ORDER BY CASE WHEN deliver_after_utc IS NULL THEN julianday(created_utc)
                      ELSE julianday(deliver_after_utc) END ASC,
                  julianday(created_utc) ASC`,
      )
      .bind(deviceId, claimToken),
  ]);
  const claimedRows = (selected[1]?.results ?? []) as MessageRow[];
  return { claimed: pageFromRows(claimedRows), hadCandidates: true };
}

function pageFromRows(rows: MessageRow[]): EncryptedEnvelopeV1[] {
  const encoder = new TextEncoder();
  const page: EncryptedEnvelopeV1[] = [];
  let pageBytes = encoder.encode(`{"messages":[]}`).length;
  for (const row of rows) {
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
  const results = await db.batch([
    db.prepare(`UPDATE message_status SET state = 'delivered', updated_utc = ?1, expires_utc = ?2
      WHERE id = ?3 AND device_id = ?4 AND state = 'queued'
        AND EXISTS (SELECT 1 FROM message_ownership WHERE id = ?3 AND device_id = ?4)`)
      .bind(nowIso, statusExpiresUtc, id, deviceId),
    db.prepare(`UPDATE message_ownership SET state = 'delivered', expires_utc = MAX(expires_utc, ?3)
      WHERE id = ?1 AND device_id = ?2 AND state = 'queued'`)
      .bind(id, deviceId, statusExpiresUtc),
    db.prepare(`DELETE FROM messages WHERE device_id = ?1 AND id = ?2
      AND EXISTS (SELECT 1 FROM message_ownership WHERE id = ?2 AND device_id = ?1 AND state = 'delivered')`)
      .bind(deviceId, id),
    db.prepare(`SELECT 1 FROM message_ownership WHERE id = ?1 AND device_id = ?2 AND state = 'delivered'`)
      .bind(id, deviceId),
  ]);
  return results[3].results.length > 0 ? "ok" : "not_found";
}

/** A `DELETE FROM messages` statement for one device, for callers (key rotation, device deletion)
 * that must include it in their own atomic `db.batch`. */
export function deleteMessagesStatement(db: D1Database, deviceId: string): D1PreparedStatement {
  return db.prepare(`DELETE FROM messages WHERE device_id = ?1`).bind(deviceId);
}

/** A `DELETE FROM message_status` statement for one device, for the device-deletion and
 * key-rotation batches (both revoke every sender session that could have read these rows). */
export function deleteMessageStatusesStatement(db: D1Database, deviceId: string): D1PreparedStatement {
  return db.prepare(`DELETE FROM message_status WHERE device_id = ?1`).bind(deviceId);
}

/** A `DELETE FROM message_ownership` statement for the device-deletion and key-rotation batches. */
export function deleteMessageOwnershipStatement(db: D1Database, deviceId: string): D1PreparedStatement {
  return db.prepare(`DELETE FROM message_ownership WHERE device_id = ?1`).bind(deviceId);
}

/** Scheduled cleanup: ciphertext rows past their retention expiry, regardless of delivery state. */
export async function deleteExpiredMessages(db: D1Database, nowIso: string): Promise<void> {
  await db.prepare(`DELETE FROM messages WHERE expires_utc < ?1`).bind(nowIso).run();
}

/** Scheduled cleanup: status rows past their own expiry — the ciphertext retention while still
 * queued (set by `insertQueuedMessage`), or 24h after delivery once `ackMessage` re-sets it. */
export async function deleteExpiredMessageStatuses(db: D1Database, nowIso: string): Promise<void> {
  await db.prepare(`DELETE FROM message_status WHERE expires_utc < ?1`).bind(nowIso).run();
}

/** Ownership is retained through ciphertext retention, so an id cannot be recycled while stale
 * ciphertext is still recoverable. Cleanup runs after message deletion in `cleanup.ts`. */
export async function deleteExpiredMessageOwnership(db: D1Database, nowIso: string): Promise<void> {
  await db.prepare(`DELETE FROM message_ownership WHERE expires_utc < ?1`).bind(nowIso).run();
}

/** Persistence for the `pairing_codes` table: one-time, 10-minute, attempt-capped codes. */

const MAX_PAIRING_ATTEMPTS = 5;

export async function insertPairingCode(
  db: D1Database,
  pairing: { codeHash: string; deviceId: string; expiresUtc: string },
): Promise<void> {
  await db
    .prepare(
      `INSERT INTO pairing_codes (code_hash, device_id, expires_utc, consumed_utc, attempt_count)
       VALUES (?1, ?2, ?3, NULL, 0)`,
    )
    .bind(pairing.codeHash, pairing.deviceId, pairing.expiresUtc)
    .run();
}

/**
 * Caps the number of live pairing codes per device: deletes every still-unredeemed code for the
 * device before a fresh one is minted, so at most one active code exists at a time. Without
 * this, repeated minting would accumulate a growing set of simultaneously-valid codes, widening
 * the guessing surface (each live code is an independent redeem oracle) and leaving stale codes
 * redeemable long after the user moved on. Consumed codes are history and are left alone for
 * the scheduled expiry sweep.
 */
export async function deleteUnredeemedPairingCodesForDevice(
  db: D1Database,
  deviceId: string,
): Promise<void> {
  await db
    .prepare(`DELETE FROM pairing_codes WHERE device_id = ?1 AND consumed_utc IS NULL`)
    .bind(deviceId)
    .run();
}

export interface PairingAttemptResult {
  attemptCount: number;
  /** True once this attempt pushed `attempt_count` past `MAX_PAIRING_ATTEMPTS`. */
  lockedOut: boolean;
}

/**
 * Atomically increments `attempt_count` for `codeHash`. If that increment pushes the count past
 * `MAX_PAIRING_ATTEMPTS` and the code was not already consumed, this also stamps `consumed_utc`,
 * permanently invalidating the code — the "6th attempt locks it out" rule. Returns `null` if no
 * pairing code with that hash exists (caller should treat this the same as 410).
 *
 * Must be called, and must fail closed, before the guarded consume-and-create-session batch, and
 * only after the request has already passed the per-IP redeem rate limit.
 */
export async function incrementPairingAttempt(
  db: D1Database,
  codeHash: string,
  nowIso: string,
): Promise<PairingAttemptResult | null> {
  const row = await db
    .prepare(
      `UPDATE pairing_codes
       SET attempt_count = attempt_count + 1,
           consumed_utc = CASE
             WHEN consumed_utc IS NULL AND attempt_count + 1 > ?2 THEN ?3
             ELSE consumed_utc
           END
       WHERE code_hash = ?1
       RETURNING attempt_count, consumed_utc`,
    )
    .bind(codeHash, MAX_PAIRING_ATTEMPTS, nowIso)
    .first<{ attempt_count: number; consumed_utc: string | null }>();
  if (!row) {
    return null;
  }
  return { attemptCount: row.attempt_count, lockedOut: row.attempt_count > MAX_PAIRING_ATTEMPTS };
}

export interface RedeemedPairing {
  deviceId: string;
  publicKeySpki: string;
}

/**
 * Consumes a pairing code and creates its sender session in one atomic `db.batch`, guarded by
 * `consumed_utc IS NULL AND expires_utc > nowIso`. The winner writes its session id into the
 * code row before the session insert; the insert is then guarded by that exact id. A timestamp
 * alone is not a safe winner marker because two simultaneous redemptions can share the same
 * millisecond. If the guard does not hold (code unknown, already consumed, or expired), nothing
 * changes and this returns `null`.
 */
export async function redeemPairingCode(
  db: D1Database,
  params: {
    codeHash: string;
    nowIso: string;
    sessionId: string;
    sessionTokenHash: string;
    sessionExpiresUtc: string;
  },
): Promise<RedeemedPairing | null> {
  const { codeHash, nowIso, sessionId, sessionTokenHash, sessionExpiresUtc } = params;
  const results = await db.batch([
    db
      .prepare(
        `UPDATE pairing_codes SET consumed_utc = ?1, consumed_session_id = ?2
         WHERE code_hash = ?3 AND consumed_utc IS NULL AND expires_utc > ?1`,
      )
      .bind(nowIso, sessionId, codeHash),
    db
      .prepare(
        `INSERT INTO sender_sessions (id, device_id, token_hash, created_utc, expires_utc)
         SELECT ?1, device_id, ?2, ?3, ?4 FROM pairing_codes
         WHERE code_hash = ?5 AND consumed_utc = ?3 AND consumed_session_id = ?1`,
      )
      .bind(sessionId, sessionTokenHash, nowIso, sessionExpiresUtc, codeHash),
    db
      .prepare(
        `SELECT d.id as device_id, d.public_key_spki as public_key_spki
         FROM pairing_codes pc
         JOIN devices d ON d.id = pc.device_id
         WHERE pc.code_hash = ?1 AND pc.consumed_utc = ?2 AND pc.consumed_session_id = ?3`,
      )
      .bind(codeHash, nowIso, sessionId),
  ]);
  const selectResult = results[2];
  const row = selectResult.results?.[0] as { device_id: string; public_key_spki: string } | undefined;
  return row ? { deviceId: row.device_id, publicKeySpki: row.public_key_spki } : null;
}

/** Scheduled cleanup: pairing codes past their expiry, regardless of whether consumed. */
export async function deleteExpiredPairingCodes(db: D1Database, nowIso: string): Promise<void> {
  await db.prepare(`DELETE FROM pairing_codes WHERE expires_utc < ?1`).bind(nowIso).run();
}

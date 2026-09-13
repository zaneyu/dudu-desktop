/** Persistence for the `devices` and `sender_sessions` tables. No plaintext tokens or keys. */

export interface DeviceRecord {
  id: string;
  publicKeySpki: string;
  desktopTokenHash: string;
  createdUtc: string;
  revokedUtc: string | null;
}

interface DeviceRow {
  id: string;
  public_key_spki: string;
  desktop_token_hash: string;
  created_utc: string;
  revoked_utc: string | null;
}

function fromRow(row: DeviceRow): DeviceRecord {
  return {
    id: row.id,
    publicKeySpki: row.public_key_spki,
    desktopTokenHash: row.desktop_token_hash,
    createdUtc: row.created_utc,
    revokedUtc: row.revoked_utc,
  };
}

export async function insertDevice(
  db: D1Database,
  device: { id: string; publicKeySpki: string; desktopTokenHash: string; createdUtc: string },
): Promise<void> {
  await db
    .prepare(
      `INSERT INTO devices (id, public_key_spki, desktop_token_hash, created_utc, revoked_utc)
       VALUES (?1, ?2, ?3, ?4, NULL)`,
    )
    .bind(device.id, device.publicKeySpki, device.desktopTokenHash, device.createdUtc)
    .run();
}

/** Looks up a non-revoked device by the SHA-256 hash of its bearer desktop token. */
export async function findDeviceByTokenHash(
  db: D1Database,
  desktopTokenHash: string,
): Promise<DeviceRecord | null> {
  const row = await db
    .prepare(
      `SELECT id, public_key_spki, desktop_token_hash, created_utc, revoked_utc
       FROM devices WHERE desktop_token_hash = ?1 AND revoked_utc IS NULL`,
    )
    .bind(desktopTokenHash)
    .first<DeviceRow>();
  return row ? fromRow(row) : null;
}

export async function findDeviceById(db: D1Database, deviceId: string): Promise<DeviceRecord | null> {
  const row = await db
    .prepare(
      `SELECT id, public_key_spki, desktop_token_hash, created_utc, revoked_utc
       FROM devices WHERE id = ?1`,
    )
    .bind(deviceId)
    .first<DeviceRow>();
  return row ? fromRow(row) : null;
}

export async function countActiveSenderSessions(
  db: D1Database,
  deviceId: string,
  nowIso: string,
): Promise<number> {
  const row = await db
    .prepare(
      `SELECT COUNT(*) as count FROM sender_sessions
       WHERE device_id = ?1 AND revoked_utc IS NULL AND expires_utc > ?2`,
    )
    .bind(deviceId, nowIso)
    .first<{ count: number }>();
  return row?.count ?? 0;
}

export interface RotateKeyParams {
  deviceId: string;
  newPublicKeySpki: string;
  newDesktopTokenHash: string;
  revokedUtc: string;
}

/**
 * Statements for one atomic key rotation: replace the device's public key and desktop-token
 * hash, and revoke every active sender session for the device. Returned as a list (rather than
 * run directly) so Task 18 can append a `DELETE FROM messages WHERE device_id = ?` statement to
 * the same `db.batch` once the messages table exists, keeping rotation fully atomic.
 */
export function buildRotateKeyStatements(
  db: D1Database,
  params: RotateKeyParams,
): D1PreparedStatement[] {
  return [
    db
      .prepare(`UPDATE devices SET public_key_spki = ?1, desktop_token_hash = ?2 WHERE id = ?3`)
      .bind(params.newPublicKeySpki, params.newDesktopTokenHash, params.deviceId),
    db
      .prepare(
        `UPDATE sender_sessions SET revoked_utc = ?1 WHERE device_id = ?2 AND revoked_utc IS NULL`,
      )
      .bind(params.revokedUtc, params.deviceId),
  ];
}

export async function rotateDeviceKey(db: D1Database, params: RotateKeyParams): Promise<void> {
  await db.batch(buildRotateKeyStatements(db, params));
}

export interface SenderSessionRecord {
  id: string;
  deviceId: string;
  publicKeySpki: string;
  deviceCreatedUtc: string;
}

/** Looks up an active sender session (and its device) by the SHA-256 hash of its cookie token. */
export async function findActiveSenderSessionByTokenHash(
  db: D1Database,
  tokenHash: string,
  nowIso: string,
): Promise<SenderSessionRecord | null> {
  const row = await db
    .prepare(
      `SELECT s.id as id, s.device_id as device_id, d.public_key_spki as public_key_spki,
              d.created_utc as device_created_utc
       FROM sender_sessions s
       JOIN devices d ON d.id = s.device_id
       WHERE s.token_hash = ?1 AND s.revoked_utc IS NULL AND s.expires_utc > ?2
         AND d.revoked_utc IS NULL`,
    )
    .bind(tokenHash, nowIso)
    .first<{ id: string; device_id: string; public_key_spki: string; device_created_utc: string }>();
  if (!row) {
    return null;
  }
  return {
    id: row.id,
    deviceId: row.device_id,
    publicKeySpki: row.public_key_spki,
    deviceCreatedUtc: row.device_created_utc,
  };
}

/** Revokes one active sender session by id. Returns whether a row was actually revoked. */
export async function revokeSenderSession(
  db: D1Database,
  sessionId: string,
  revokedUtc: string,
): Promise<boolean> {
  const result = await db
    .prepare(`UPDATE sender_sessions SET revoked_utc = ?1 WHERE id = ?2 AND revoked_utc IS NULL`)
    .bind(revokedUtc, sessionId)
    .run();
  return (result.meta.changes ?? 0) > 0;
}

/** Scheduled cleanup: sessions revoked or expired more than `cutoffIso` ago (24h, in practice). */
export async function deleteStaleSenderSessions(db: D1Database, cutoffIso: string): Promise<void> {
  await db
    .prepare(
      `DELETE FROM sender_sessions
       WHERE (revoked_utc IS NOT NULL AND revoked_utc < ?1) OR expires_utc < ?1`,
    )
    .bind(cutoffIso)
    .run();
}

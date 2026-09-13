import { sha256HexOfText } from "./tokens.js";

/** `CF-Connecting-IP` is set by Cloudflare's edge; tests set it explicitly. */
export function clientIpFromRequest(request: Request): string {
  return request.headers.get("CF-Connecting-IP") ?? "unknown";
}

/** Exported for `cleanup.ts`: the start of the current hourly bucket window, so cleanup can treat
 * every bucket from an earlier window as stale regardless of its count. */
export function hourWindowStartUtc(now: Date): string {
  const floored = new Date(now.getTime());
  floored.setUTCMinutes(0, 0, 0);
  return floored.toISOString();
}

/**
 * Fixed one-hour-window rate limiter backed by `rate_limit_buckets`. Atomically increments (or
 * starts a fresh window for) the bucket keyed by `SHA-256("<scope>:<clientIp>")` and reports
 * whether the request that triggered this increment is still within `limitPerHour`.
 *
 * Must be called before any lookup of the resource being protected (e.g. a pairing code), so a
 * rate-limited caller never causes an extra DB read of that resource.
 */
export async function enforceRateLimit(
  db: D1Database,
  scope: string,
  clientIp: string,
  limitPerHour: number,
  now: Date = new Date(),
): Promise<boolean> {
  const keyHash = await sha256HexOfText(`${scope}:${clientIp}`);
  const windowStart = hourWindowStartUtc(now);
  const row = await db
    .prepare(
      `INSERT INTO rate_limit_buckets (key_hash, window_start_utc, count)
       VALUES (?1, ?2, 1)
       ON CONFLICT (key_hash) DO UPDATE SET
         count = CASE
           WHEN rate_limit_buckets.window_start_utc = ?2 THEN rate_limit_buckets.count + 1
           ELSE 1
         END,
         window_start_utc = ?2
       RETURNING count`,
    )
    .bind(keyHash, windowStart)
    .first<{ count: number }>();
  const count = row?.count ?? 1;
  return count <= limitPerHour;
}

/**
 * Scheduled cleanup: buckets whose window is not the current hour. The limiter never writes
 * `count = 0` (every bucket starts at 1 and only increments, and resets to 1 rather than 0 on a
 * new window), so "empty" is interpreted as "stale": once a window is no longer current,
 * `enforceRateLimit` will never read or update that row again, so it is dead weight regardless of
 * its count. Callers pass `hourWindowStartUtc(now)` as `currentWindowStartIso`.
 */
export async function deleteStaleRateLimitBuckets(db: D1Database, currentWindowStartIso: string): Promise<void> {
  await db.prepare(`DELETE FROM rate_limit_buckets WHERE window_start_utc < ?1`).bind(currentWindowStartIso).run();
}

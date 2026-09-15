import { sha256HexOfText } from "./tokens.js";

/**
 * Trust assumption for `CF-Connecting-IP`: every request to a Worker (custom domain or
 * `*.workers.dev`) passes through Cloudflare's edge, which sets this header from the TCP peer and
 * overwrites any client-supplied value. A client therefore cannot pick its bucket by sending a
 * forged header; it can only change buckets by actually changing its source address. That is
 * still cheap (IPv6 hands out whole /64s, botnets exist), so:
 *
 * - IPv6 addresses are bucketed by their /64 prefix, so one subscriber allocation is one bucket.
 * - The unauthenticated routes an IP rotator would target (device registration, pairing redeem)
 *   ALSO carry an IP-independent global limit (see `GLOBAL_RATE_LIMIT_KEY`).
 *
 * When the header is absent (only possible outside the edge: loopback dev, tests without it), the
 * caller lands in one shared `"unknown"` bucket with a tighter cap rather than being rejected.
 */
export function clientIpFromRequest(request: Request): string {
  const raw = request.headers.get("CF-Connecting-IP")?.trim();
  if (!raw) {
    return "unknown";
  }
  return ipv6Slash64(raw) ?? raw;
}

/** The key used for IP-independent limits: one bucket per scope for every caller. */
export const GLOBAL_RATE_LIMIT_KEY = "global";

/** Returns `a:b:c:d::/64` for a parseable IPv6 address, or null for anything else (IPv4, junk). */
function ipv6Slash64(value: string): string | null {
  if (!value.includes(":")) {
    return null;
  }
  const address = value.split("%")[0];
  const halves = address.split("::");
  if (halves.length > 2) {
    return null;
  }
  const head = halves[0] ? halves[0].split(":") : [];
  const tail = halves.length === 2 && halves[1] ? halves[1].split(":") : [];
  // An embedded IPv4 tail (::ffff:1.2.3.4) occupies two groups; it never affects the /64 prefix.
  const tailGroups = tail.reduce((sum, group) => sum + (group.includes(".") ? 2 : 1), 0);
  const missing = 8 - head.length - tailGroups;
  if ((halves.length === 1 && missing !== 0) || missing < 0) {
    return null;
  }
  const groups = [...head, ...Array<string>(halves.length === 2 ? missing : 0).fill("0"), ...tail];
  const prefix = groups.slice(0, 4);
  if (!prefix.every((group) => /^[0-9a-fA-F]{1,4}$/.test(group))) {
    return null;
  }
  return `${prefix.map((group) => parseInt(group, 16).toString(16)).join(":")}::/64`;
}

const HOUR_MS = 60 * 60 * 1000;

/** Exported for `cleanup.ts`: the start of the current hourly bucket window. */
export function hourWindowStartUtc(now: Date): string {
  const floored = new Date(now.getTime());
  floored.setUTCMinutes(0, 0, 0);
  return floored.toISOString();
}

/**
 * Collective hourly cap for the shared `"unknown"` bucket (see `clientIpFromRequest`). Five per
 * hour is enough for loopback development and tight enough that headerless abuse cannot scale.
 */
const UNKNOWN_IP_GLOBAL_LIMIT_PER_HOUR = 5;

interface BucketTarget {
  keyHash: string;
  limit: number;
  windowStart: string;
  previousWindowStart: string;
  /** Weight of the previous window's count: 1 at the top of the hour, 0 at its end. */
  previousWeight: number;
}

async function bucketTarget(scope: string, key: string, limitPerHour: number, now: Date): Promise<BucketTarget> {
  const isUnknownIp = key.trim().length === 0 || key === "unknown";
  const bucketScope = isUnknownIp ? `${scope}:unknown-global` : `${scope}:${key}`;
  const windowStartMs = Date.parse(hourWindowStartUtc(now));
  return {
    keyHash: await sha256HexOfText(bucketScope),
    limit: isUnknownIp ? Math.min(limitPerHour, UNKNOWN_IP_GLOBAL_LIMIT_PER_HOUR) : limitPerHour,
    windowStart: new Date(windowStartMs).toISOString(),
    previousWindowStart: new Date(windowStartMs - HOUR_MS).toISOString(),
    previousWeight: 1 - (now.getTime() - windowStartMs) / HOUR_MS,
  };
}

/**
 * Sliding one-hour rate limiter backed by `rate_limit_buckets`, using the two-window
 * approximation `previous * (fraction of the previous hour still inside the last 60 minutes) +
 * current`. Unlike a plain fixed window this does not allow a 2x burst across an hour boundary.
 * Atomically counts this request (denied requests count too) and reports whether it is within
 * `limitPerHour`. Keyed by `SHA-256("<scope>:<key>")`; the `"unknown"` key shares one tighter
 * bucket per scope.
 *
 * Must be called before any lookup of the resource being protected.
 */
export async function enforceRateLimit(
  db: D1Database,
  scope: string,
  key: string,
  limitPerHour: number,
  now: Date = new Date(),
): Promise<boolean> {
  const target = await bucketTarget(scope, key, limitPerHour, now);
  // SQLite evaluates every SET expression against the pre-update row, so `previous_count` sees
  // the old `count` and `window_start_utc`.
  const row = await db
    .prepare(
      `INSERT INTO rate_limit_buckets (key_hash, window_start_utc, count, previous_count)
       VALUES (?1, ?2, 1, 0)
       ON CONFLICT (key_hash) DO UPDATE SET
         previous_count = CASE
           WHEN rate_limit_buckets.window_start_utc = ?2 THEN rate_limit_buckets.previous_count
           WHEN rate_limit_buckets.window_start_utc = ?3 THEN rate_limit_buckets.count
           ELSE 0
         END,
         count = CASE
           WHEN rate_limit_buckets.window_start_utc = ?2 THEN rate_limit_buckets.count + 1
           ELSE 1
         END,
         window_start_utc = ?2
       RETURNING count, previous_count`,
    )
    .bind(target.keyHash, target.windowStart, target.previousWindowStart)
    .first<{ count: number; previous_count: number }>();
  const estimate = (row?.previous_count ?? 0) * target.previousWeight + (row?.count ?? 1);
  return estimate <= target.limit;
}

/**
 * Read-only companion to `enforceRateLimit`: true when the bucket has already used up its
 * allowance, without counting this request. Used for failure counters, where only failures are
 * recorded (via `enforceRateLimit`) but every request must first be checked against them.
 */
export async function isRateLimitExhausted(
  db: D1Database,
  scope: string,
  key: string,
  limitPerHour: number,
  now: Date = new Date(),
): Promise<boolean> {
  const target = await bucketTarget(scope, key, limitPerHour, now);
  const row = await db
    .prepare(`SELECT window_start_utc, count, previous_count FROM rate_limit_buckets WHERE key_hash = ?1`)
    .bind(target.keyHash)
    .first<{ window_start_utc: string; count: number; previous_count: number }>();
  if (!row) {
    return false;
  }
  let current = 0;
  let previous = 0;
  if (row.window_start_utc === target.windowStart) {
    current = row.count;
    previous = row.previous_count;
  } else if (row.window_start_utc === target.previousWindowStart) {
    previous = row.count;
  }
  return previous * target.previousWeight + current >= target.limit;
}

/**
 * Scheduled cleanup: buckets older than the previous hourly window. The previous window must be
 * kept because the sliding estimate still reads its count. Callers pass `hourWindowStartUtc(now)`.
 */
export async function deleteStaleRateLimitBuckets(db: D1Database, currentWindowStartIso: string): Promise<void> {
  const previousWindowStartIso = new Date(Date.parse(currentWindowStartIso) - HOUR_MS).toISOString();
  await db.prepare(`DELETE FROM rate_limit_buckets WHERE window_start_utc < ?1`).bind(previousWindowStartIso).run();
}

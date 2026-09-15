import { env, exports } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { cleanupExpired } from "../src/cleanup.js";
import type { Env } from "../src/env.js";
import { validateIncomingEnvelope } from "../src/security/envelopeValidation.js";
import { isSafeCookieGet, isSameOrigin } from "../src/security/origin.js";
import {
  clientIpFromRequest,
  enforceRateLimit,
  GLOBAL_RATE_LIMIT_KEY,
  hourWindowStartUtc,
  isRateLimitExhausted,
} from "../src/security/rateLimit.js";
import { generatePairingCode, sha256HexOfText } from "../src/security/tokens.js";
import {
  createPairingCode,
  fetchWorker,
  jsonHeaders,
  pairedFixture,
  pairedFixtureWithMessage,
  redeem,
  registerDevice,
  TEST_PUBLIC_KEY,
  validEnvelope,
} from "./helpers.js";

const testEnv = env as unknown as Env;
const BASE_URL = "https://example.test";
const HOUR_MS = 60 * 60 * 1000;

async function fillBucket(scope: string, key: string, count: number): Promise<void> {
  await testEnv.DB.prepare(
    `INSERT INTO rate_limit_buckets (key_hash, window_start_utc, count, previous_count) VALUES (?1, ?2, ?3, 0)`,
  )
    .bind(await sha256HexOfText(`${scope}:${key}`), hourWindowStartUtc(new Date()), count)
    .run();
}

function withHeaders(headers: Record<string, string>): Request {
  return new Request(`${BASE_URL}/v1/sender/device`, { headers });
}

describe("rate limiting", () => {
  it("buckets IPv6 callers by /64 and a missing header as unknown", () => {
    const ip = (value?: string) =>
      clientIpFromRequest(new Request(BASE_URL, { headers: value ? { "CF-Connecting-IP": value } : {} }));
    expect(ip("2001:db8:1:2:aaaa::1")).toBe(ip("2001:0db8:0001:0002:bbbb:cccc:dddd:eeee"));
    expect(ip("2001:db8:1:2::1")).not.toBe(ip("2001:db8:1:3::1"));
    expect(ip("203.0.113.7")).toBe("203.0.113.7");
    expect(ip()).toBe("unknown");
    expect(ip("   ")).toBe("unknown");
  });

  it("gives headerless callers one shared, tighter bucket", async () => {
    for (let index = 0; index < 5; index += 1) {
      expect(await enforceRateLimit(testEnv.DB, "test-unknown", "unknown", 100)).toBe(true);
    }
    expect(await enforceRateLimit(testEnv.DB, "test-unknown", "unknown", 100)).toBe(false);
  });

  it("uses a sliding window, so a full allowance cannot be spent on both sides of the hour", async () => {
    const at = (iso: string) => new Date(iso);
    for (let index = 0; index < 3; index += 1) {
      expect(await enforceRateLimit(testEnv.DB, "test-sliding", "k", 3, at("2026-01-01T00:59:00.000Z"))).toBe(true);
    }
    // A fixed window would reset at 01:00; the previous hour's 3 requests still weigh ~2.95.
    expect(await enforceRateLimit(testEnv.DB, "test-sliding", "k", 3, at("2026-01-01T01:01:00.000Z"))).toBe(false);
    // Two hours later both windows have aged out.
    expect(await enforceRateLimit(testEnv.DB, "test-sliding", "k", 3, at("2026-01-01T03:00:00.000Z"))).toBe(true);
  });

  it("isRateLimitExhausted peeks without counting", async () => {
    expect(await isRateLimitExhausted(testEnv.DB, "test-peek", "k", 1)).toBe(false);
    expect(await isRateLimitExhausted(testEnv.DB, "test-peek", "k", 1)).toBe(false);
    expect(await enforceRateLimit(testEnv.DB, "test-peek", "k", 1)).toBe(true);
    expect(await isRateLimitExhausted(testEnv.DB, "test-peek", "k", 1)).toBe(true);
  });

  it("caps device registration globally, independent of the caller's IP", async () => {
    await fillBucket("register-device", GLOBAL_RATE_LIMIT_KEY, 20);
    const response = await exports.default.fetch(
      new Request(`${BASE_URL}/v1/devices/register`, {
        method: "POST",
        headers: jsonHeaders({ "CF-Connecting-IP": "198.51.100.1" }),
        body: JSON.stringify({ publicKey: TEST_PUBLIC_KEY }),
      }),
    );
    expect(response.status).toBe(429);
  });

  it("locks redeem globally after 20 failures from rotating IPs, even for a valid code", async () => {
    const registration = await registerDevice();
    const pairing = await createPairingCode(registration.desktopToken);
    for (let index = 0; index < 20; index += 1) {
      const failed = await redeem(generatePairingCode());
      expect(failed.status).toBe(410);
    }
    const blocked = await redeem(pairing.code);
    expect(blocked.status).toBe(429);
  });
});

describe("message status lifetime", () => {
  it("gives a queued status the same expiry as its ciphertext", async () => {
    const paired = await pairedFixtureWithMessage();
    const row = await testEnv.DB.prepare(
      `SELECT s.expires_utc AS status_expires, m.expires_utc AS message_expires
       FROM message_status s JOIN messages m ON m.id = s.id WHERE s.id = ?1`,
    )
      .bind(paired.messageId)
      .first<{ status_expires: string; message_expires: string }>();
    expect(row?.status_expires).toBe(row?.message_expires);
  });

  it("still reports delivered after the delivered status row is pruned", async () => {
    const paired = await pairedFixtureWithMessage();
    await paired.desktop.poll();
    expect((await paired.desktop.ack(paired.messageId)).status).toBe(204);

    await cleanupExpired(testEnv, new Date(Date.now() + 25 * HOUR_MS));

    const statusRow = await testEnv.DB.prepare("SELECT 1 FROM message_status WHERE id = ?1")
      .bind(paired.messageId)
      .first();
    expect(statusRow).toBeNull();
    expect(await paired.sender.status(paired.messageId)).toEqual({ status: "delivered" });
  });

  it("key rotation clears status rows so a re-paired sender can resubmit the same id", async () => {
    const paired = await pairedFixture();
    const messageId = crypto.randomUUID();
    const envelope = await validEnvelope({ messageId });
    expect((await paired.sender.postMessage(envelope)).status).toBe(202);

    const rotate = await fetchWorker("/v1/devices/current/rotate-key", {
      method: "POST",
      headers: jsonHeaders({ Authorization: `Bearer ${paired.desktopToken}` }),
      body: JSON.stringify({ publicKey: TEST_PUBLIC_KEY }),
    });
    expect(rotate.status).toBe(200);
    const { desktopToken } = await rotate.json<{ desktopToken: string }>();
    const statusRow = await testEnv.DB.prepare("SELECT 1 FROM message_status WHERE id = ?1").bind(messageId).first();
    expect(statusRow).toBeNull();

    const pairing = await createPairingCode(desktopToken);
    const redeemed = await redeem(pairing.code);
    const cookie = redeemed.headers.get("set-cookie")?.split(";")[0] ?? "";
    const resubmit = await fetchWorker("/v1/messages", {
      method: "POST",
      headers: jsonHeaders({ Origin: BASE_URL, Cookie: cookie }),
      body: JSON.stringify(envelope),
    });
    expect(resubmit.status).toBe(202);
    const status = await fetchWorker(`/v1/messages/${messageId}/status`, { headers: { Cookie: cookie } });
    expect(await status.json()).toEqual({ status: "queued" });
  });
});

describe("createdUtc strictness", () => {
  it("rejects an impossible calendar date that Date.parse would roll over", async () => {
    await pairedFixture();
    const base = await validEnvelope({ messageId: crypto.randomUUID() });
    const now = new Date("2026-03-02T00:10:00.000Z");
    await expect(validateIncomingEnvelope({ ...base, createdUtc: "2026-02-30T00:00:00Z" }, now)).rejects.toThrow();
    await expect(validateIncomingEnvelope({ ...base, createdUtc: "2026-03-01T24:00:00Z" }, now)).rejects.toThrow();
    await expect(validateIncomingEnvelope({ ...base, createdUtc: "2026-03-02T00:00:00.5Z" }, now)).resolves.toBeTruthy();
  });
});

describe("origin checks", () => {
  it("normalizes equivalent Origin spellings but rejects null and non-bare values", () => {
    const post = (origin: string) =>
      new Request(`${BASE_URL}/v1/pairings/redeem`, { method: "POST", headers: { Origin: origin } });
    expect(isSameOrigin(post(BASE_URL))).toBe(true);
    expect(isSameOrigin(post("https://EXAMPLE.test:443"))).toBe(true);
    expect(isSameOrigin(post("null"))).toBe(false);
    expect(isSameOrigin(post("https://example.test/path"))).toBe(false);
    expect(isSameOrigin(post("https://evil.example"))).toBe(false);
    expect(isSameOrigin(post("http://example.test"))).toBe(false);
  });

  it("lets Fetch Metadata decide cookie GETs when the browser sends it", () => {
    expect(isSafeCookieGet(withHeaders({ "Sec-Fetch-Site": "same-origin", Origin: "null" }))).toBe(true);
    expect(isSafeCookieGet(withHeaders({ "Sec-Fetch-Site": "none" }))).toBe(true);
    expect(isSafeCookieGet(withHeaders({ "Sec-Fetch-Site": "cross-site" }))).toBe(false);
    expect(isSafeCookieGet(withHeaders({ "Sec-Fetch-Site": "same-site", Origin: BASE_URL }))).toBe(false);
    expect(isSafeCookieGet(withHeaders({ Origin: "https://evil.example" }))).toBe(false);
    expect(isSafeCookieGet(withHeaders({}))).toBe(true);
  });
});

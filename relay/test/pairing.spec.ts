import { env, exports } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { hmacSha256Hex } from "../src/security/tokens.js";
import {
  createPairingCode,
  createPairingCodeForTest,
  fetchWorker,
  jsonHeaders,
  randomTestIp,
  redeem,
  registerDevice,
  TEST_PUBLIC_KEY,
} from "./helpers.js";

const testEnv = env as unknown as { DB: D1Database; PAIRING_CODE_PEPPER: string };

const ORIGIN = "https://example.test";

describe("device registration and pairing", () => {
  it("redeems a pairing code once and sets a protected sender cookie", async () => {
    const registration = await registerDevice();
    const pairing = await createPairingCode(registration.desktopToken);

    const first = await exports.default.fetch(
      new Request("https://example.test/v1/pairings/redeem", {
        method: "POST",
        headers: jsonHeaders({ Origin: "https://example.test", "CF-Connecting-IP": randomTestIp() }),
        body: JSON.stringify({ code: pairing.code }),
      }),
    );
    const second = await redeem(pairing.code);

    expect(first.status).toBe(200);
    expect(first.headers.get("set-cookie")).toMatch(/HttpOnly; Secure; SameSite=Strict/);
    expect((await first.json<{ publicKey: string }>()).publicKey).toBe(TEST_PUBLIC_KEY);
    expect(second.status).toBe(410);
  });

  it("expires pairing codes after ten minutes", async () => {
    const { code } = await createPairingCodeForTest({ ageSeconds: 601 });
    expect((await redeem(code)).status).toBe(410);
  });

  it("stores hashes rather than raw capability tokens", async () => {
    const { desktopToken } = await registerDevice();
    const row = await env.DB.prepare("SELECT desktop_token_hash FROM devices").first<{
      desktop_token_hash: string;
    }>();
    expect(row?.desktop_token_hash).not.toContain(desktopToken);
  });

  it("rate limits device registration to five requests per IP per hour", async () => {
    const ip = "203.0.113.10";
    for (let i = 0; i < 5; i++) {
      const response = await fetchWorker("/v1/devices/register", {
        method: "POST",
        headers: jsonHeaders({ "CF-Connecting-IP": ip }),
        body: JSON.stringify({ publicKey: TEST_PUBLIC_KEY }),
      });
      expect(response.status).toBe(201);
    }
    const sixth = await fetchWorker("/v1/devices/register", {
      method: "POST",
      headers: jsonHeaders({ "CF-Connecting-IP": ip }),
      body: JSON.stringify({ publicKey: TEST_PUBLIC_KEY }),
    });
    expect(sixth.status).toBe(429);
  });

  it("locks out an expired pairing code's record after five attempts against it", async () => {
    // An expired code never reaches the consumed_utc-setting branch in redeemPairingCode, so
    // repeated attempts against the SAME (correctly-typed) code keep incrementing its
    // attempt_count via incrementPairingAttempt until the 6th attempt trips the cap and stamps
    // consumed_utc itself -- distinct from the plain "already expired" 410 each attempt already
    // gets. A guessed/wrong code cannot exercise this path: it hashes to no row at all.
    const { code } = await createPairingCodeForTest({ ageSeconds: 601 });

    for (let i = 0; i < 6; i++) {
      const response = await redeem(code, { "CF-Connecting-IP": "198.51.100.50" });
      expect(response.status).toBe(410);
    }

    const codeHash = await hmacSha256Hex(testEnv.PAIRING_CODE_PEPPER, code);
    const row = await testEnv.DB.prepare(
      "SELECT attempt_count, consumed_utc FROM pairing_codes WHERE code_hash = ?1",
    )
      .bind(codeHash)
      .first<{ attempt_count: number; consumed_utc: string | null }>();
    expect(row?.attempt_count).toBeGreaterThanOrEqual(6);
    expect(row?.consumed_utc).not.toBeNull();
  });

  it("rejects pairing redemption from a mismatched Origin", async () => {
    const registration = await registerDevice();
    const pairing = await createPairingCode(registration.desktopToken);

    const response = await exports.default.fetch(
      new Request("https://example.test/v1/pairings/redeem", {
        method: "POST",
        headers: jsonHeaders({ Origin: "https://evil.test", "CF-Connecting-IP": randomTestIp() }),
        body: JSON.stringify({ code: pairing.code }),
      }),
    );

    expect(response.status).toBe(403);
  });

  it("disconnect revokes the sender session and clears the cookie", async () => {
    const registration = await registerDevice();
    const pairing = await createPairingCode(registration.desktopToken);
    const redeemed = await redeem(pairing.code);
    const cookie = redeemed.headers.get("set-cookie")?.split(";")[0];
    expect(cookie).toBeTruthy();

    const disconnect = await exports.default.fetch(
      new Request("https://example.test/v1/sender/disconnect", {
        method: "POST",
        headers: jsonHeaders({ Origin: ORIGIN, Cookie: cookie ?? "" }),
      }),
    );
    expect(disconnect.status).toBe(204);
    expect(disconnect.headers.get("set-cookie")).toMatch(/Max-Age=0/);

    const afterDisconnect = await exports.default.fetch(
      new Request("https://example.test/v1/sender/device", {
        headers: { Cookie: cookie ?? "" },
      }),
    );
    expect(afterDisconnect.status).toBe(401);
  });

  it("rotating a device's key revokes sender sessions and invalidates the old token", async () => {
    const registration = await registerDevice();
    const pairing = await createPairingCode(registration.desktopToken);
    const redeemed = await redeem(pairing.code);
    const cookie = redeemed.headers.get("set-cookie")?.split(";")[0] ?? "";

    const rotateResponse = await fetchWorker("/v1/devices/current/rotate-key", {
      method: "POST",
      headers: jsonHeaders({ Authorization: `Bearer ${registration.desktopToken}` }),
      body: JSON.stringify({ publicKey: TEST_PUBLIC_KEY }),
    });
    expect(rotateResponse.status).toBe(200);
    const { desktopToken: newDesktopToken } = await rotateResponse.json<{ desktopToken: string }>();
    expect(newDesktopToken).not.toBe(registration.desktopToken);

    const oldTokenResponse = await fetchWorker("/v1/devices/current", {
      headers: { Authorization: `Bearer ${registration.desktopToken}` },
    });
    expect(oldTokenResponse.status).toBe(401);

    const newTokenResponse = await fetchWorker("/v1/devices/current", {
      headers: { Authorization: `Bearer ${newDesktopToken}` },
    });
    expect(newTokenResponse.status).toBe(200);

    const senderAfterRotation = await exports.default.fetch(
      new Request("https://example.test/v1/sender/device", {
        headers: { Cookie: cookie },
      }),
    );
    expect(senderAfterRotation.status).toBe(401);
  });

  it("GET /v1/devices/current never contains the token or the SPKI public key", async () => {
    const registration = await registerDevice();
    const response = await fetchWorker("/v1/devices/current", {
      headers: { Authorization: `Bearer ${registration.desktopToken}` },
    });
    expect(response.status).toBe(200);
    const text = await response.text();
    expect(text).not.toContain(registration.desktopToken);
    expect(text).not.toContain(TEST_PUBLIC_KEY);
    const body = JSON.parse(text) as Record<string, unknown>;
    expect(Object.keys(body).sort()).toEqual(["activeSenderSessions", "createdUtc", "publicKeyFingerprint"]);
  });
});

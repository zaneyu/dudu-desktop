/**
 * Generic Worker-runtime test helpers shared by `pairing.spec.ts` (this task) and Task 18's
 * message-relay tests. Every helper drives the Worker the same way production traffic does —
 * through `exports.default.fetch(...)` — except `createPairingCodeForTest`, which reaches into
 * `env.DB` directly to construct an otherwise-unreachable backdated row.
 */
import { env, exports } from "cloudflare:workers";
import { createRecipientForTest } from "../sender-src/crypto.js";
import type {
  CreatePairingCodeResponse,
  RegisterDeviceResponse,
} from "../src/protocol/types.js";
import { bytesToBase64Url, generatePairingCode, hmacSha256Hex } from "../src/security/tokens.js";

const BASE_URL = "https://example.test";
const PAIRING_CODE_VALIDITY_MS = 10 * 60_000;

/** A real, once-generated P-256 SPKI Base64URL public key, so every test that imports it as a
 * key actually can — a hand-typed string would fail `crypto.subtle.importKey`. */
export const TEST_PUBLIC_KEY = await (async () => {
  const { publicKey } = await createRecipientForTest();
  const spki = await crypto.subtle.exportKey("spki", publicKey);
  return bytesToBase64Url(new Uint8Array(spki));
})();

export function jsonHeaders(extra?: Record<string, string>): Headers {
  return new Headers({ "Content-Type": "application/json", ...extra });
}

/**
 * The Workers vitest pool does not reset D1/`rate_limit_buckets` state between individual `it()`s
 * in the same spec file (only, at most, between files) despite storage isolation being on by
 * default -- so every helper that hits a rate-limited route defaults to a fresh, per-call
 * `CF-Connecting-IP` unless the caller passes its own (as the rate-limit tests do, deliberately
 * reusing one IP). This keeps unrelated tests from tripping each other's rate limits.
 */
function randomTestIp(): string {
  return crypto.randomUUID();
}

export async function registerDevice(
  publicKey: string = TEST_PUBLIC_KEY,
  extraHeaders?: Record<string, string>,
): Promise<RegisterDeviceResponse> {
  const response = await exports.default.fetch(
    new Request(`${BASE_URL}/v1/devices/register`, {
      method: "POST",
      headers: jsonHeaders({ "CF-Connecting-IP": randomTestIp(), ...extraHeaders }),
      body: JSON.stringify({ publicKey }),
    }),
  );
  if (response.status !== 201) {
    throw new Error(`registerDevice failed with status ${response.status}`);
  }
  return response.json();
}

export async function createPairingCode(desktopToken: string): Promise<CreatePairingCodeResponse> {
  const response = await exports.default.fetch(
    new Request(`${BASE_URL}/v1/devices/pairing-code`, {
      method: "POST",
      headers: jsonHeaders({ Authorization: `Bearer ${desktopToken}` }),
    }),
  );
  if (response.status !== 201) {
    throw new Error(`createPairingCode failed with status ${response.status}`);
  }
  return response.json();
}

export async function redeem(code: string, extraHeaders?: Record<string, string>): Promise<Response> {
  return exports.default.fetch(
    new Request(`${BASE_URL}/v1/pairings/redeem`, {
      method: "POST",
      headers: jsonHeaders({ Origin: BASE_URL, "CF-Connecting-IP": randomTestIp(), ...extraHeaders }),
      body: JSON.stringify({ code }),
    }),
  );
}

/**
 * Inserts a pairing code straight through `env.DB`, bypassing HTTP so the test can backdate it.
 * `ageSeconds` is how long ago the code was "created": with the production 10-minute validity
 * window, `ageSeconds: 601` yields a code that expired one second ago.
 */
export async function createPairingCodeForTest(
  options: { ageSeconds: number },
): Promise<{ code: string; deviceId: string }> {
  const { deviceId } = await registerDevice();
  const code = generatePairingCode();
  const testEnv = env as unknown as { DB: D1Database; PAIRING_CODE_PEPPER: string };
  const codeHash = await hmacSha256Hex(testEnv.PAIRING_CODE_PEPPER, code);
  const createdAtMs = Date.now() - options.ageSeconds * 1000;
  const expiresUtc = new Date(createdAtMs + PAIRING_CODE_VALIDITY_MS).toISOString();
  await testEnv.DB.prepare(
    `INSERT INTO pairing_codes (code_hash, device_id, expires_utc, consumed_utc, attempt_count)
     VALUES (?1, ?2, ?3, NULL, 0)`,
  )
    .bind(codeHash, deviceId, expiresUtc)
    .run();
  return { code, deviceId };
}

export function fetchWorker(path: string, init?: RequestInit): Promise<Response> {
  return exports.default.fetch(new Request(`${BASE_URL}${path}`, init));
}

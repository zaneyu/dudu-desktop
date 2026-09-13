/**
 * Generic Worker-runtime test helpers shared by `pairing.spec.ts` (this task) and Task 18's
 * message-relay tests. Every helper drives the Worker the same way production traffic does —
 * through `exports.default.fetch(...)` — except `createPairingCodeForTest` and
 * `messageCiphertext`, which reach into `env.DB` directly to construct/inspect rows the HTTP
 * surface deliberately never exposes.
 */
import { env, exports } from "cloudflare:workers";
import { router } from "../src/index.js";
import { createRecipientForTest, encryptPayload } from "../sender-src/crypto.js";
import type {
  CreatePairingCodeResponse,
  EncryptedEnvelopeV1,
  RegisterDeviceResponse,
  RemoteMessagePayloadV1,
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
export function randomTestIp(): string {
  return crypto.randomUUID();
}

/** Fixed reference point for schedule-related assertions (`addMinutes(now(), 5)`, etc). */
export function now(): Date {
  return new Date();
}

export function addMinutes(date: Date, minutes: number): string {
  return new Date(date.getTime() + minutes * 60_000).toISOString();
}

/**
 * Flat list of registered route paths (method-agnostic), so a test can assert e.g. that no
 * `/v1/messages/:id/read` route exists regardless of which HTTP method it might have used.
 */
export function allRegisteredRoutes(): string[] {
  return router.allRegisteredRoutes().map((route) => route.path);
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

/**
 * `validEnvelope()` has no fixture argument (it matches the brief's verbatim test shape), so it
 * encrypts to whichever recipient key `pairedFixture()` most recently generated and registered.
 * Tests that need more than one fixture concurrently should call `pairedFixture()` again
 * immediately before building each fixture's envelopes.
 */
let activeRecipientPublicKey: CryptoKey | null = null;

export interface PairedFixture {
  deviceId: string;
  desktopToken: string;
  sessionCookie: string;
  sender: {
    postMessage(envelope: EncryptedEnvelopeV1): Promise<Response>;
    status(messageId: string): Promise<unknown>;
  };
  desktop: {
    poll(): Promise<EncryptedEnvelopeV1[]>;
    ack(messageId: string): Promise<Response>;
  };
}

/** Registers a fresh device, pairs it, and redeems the pairing code into a sender-session cookie. */
export async function pairedFixture(): Promise<PairedFixture> {
  const { publicKey } = await createRecipientForTest();
  activeRecipientPublicKey = publicKey;
  const spki = await crypto.subtle.exportKey("spki", publicKey);
  const recipientPublicKey = bytesToBase64Url(new Uint8Array(spki));

  const registration = await registerDevice(recipientPublicKey);
  const pairing = await createPairingCode(registration.desktopToken);
  const redeemed = await redeem(pairing.code);
  const sessionCookie = redeemed.headers.get("set-cookie")?.split(";")[0] ?? "";
  if (!sessionCookie) {
    throw new Error("pairedFixture: redeem did not set a session cookie.");
  }

  return {
    deviceId: registration.deviceId,
    desktopToken: registration.desktopToken,
    sessionCookie,
    sender: {
      postMessage(envelope: EncryptedEnvelopeV1): Promise<Response> {
        return exports.default.fetch(
          new Request(`${BASE_URL}/v1/messages`, {
            method: "POST",
            headers: jsonHeaders({ Origin: BASE_URL, Cookie: sessionCookie }),
            body: JSON.stringify(envelope),
          }),
        );
      },
      async status(messageId: string): Promise<unknown> {
        const response = await exports.default.fetch(
          new Request(`${BASE_URL}/v1/messages/${messageId}/status`, {
            headers: { Cookie: sessionCookie },
          }),
        );
        return response.json();
      },
    },
    desktop: {
      async poll(): Promise<EncryptedEnvelopeV1[]> {
        const response = await exports.default.fetch(
          new Request(`${BASE_URL}/v1/messages`, {
            headers: { Authorization: `Bearer ${registration.desktopToken}` },
          }),
        );
        const body = await response.json<{ messages: EncryptedEnvelopeV1[] }>();
        return body.messages;
      },
      ack(messageId: string): Promise<Response> {
        return exports.default.fetch(
          new Request(`${BASE_URL}/v1/messages/${messageId}/ack`, {
            method: "POST",
            headers: { Authorization: `Bearer ${registration.desktopToken}` },
          }),
        );
      },
    },
  };
}

/** `pairedFixture()` plus one already-queued message (202'd through the sender route). */
export async function pairedFixtureWithMessage(
  options?: { deliverAfterUtc?: string | null },
): Promise<PairedFixture & { messageId: string }> {
  const paired = await pairedFixture();
  const messageId = crypto.randomUUID();
  const envelope = await validEnvelope({ messageId, deliverAfterUtc: options?.deliverAfterUtc ?? null });
  const response = await paired.sender.postMessage(envelope);
  if (response.status !== 202) {
    throw new Error(`pairedFixtureWithMessage: postMessage failed with status ${response.status}`);
  }
  return { ...paired, messageId };
}

/**
 * Builds a REAL, fully encrypted `EncryptedEnvelopeV1` (never a stub) for whichever device
 * `pairedFixture()` most recently set up, so relay tests exercise genuine ciphertext rather than
 * a hand-built object the relay could never actually receive.
 */
export async function validEnvelope(overrides: {
  messageId: string;
  createdUtc?: string;
  deliverAfterUtc?: string | null;
  payload?: RemoteMessagePayloadV1;
}): Promise<EncryptedEnvelopeV1> {
  if (!activeRecipientPublicKey) {
    throw new Error("validEnvelope: call pairedFixture() first to establish a recipient key.");
  }
  const payload: RemoteMessagePayloadV1 = overrides.payload ?? {
    kind: "note",
    text: "hello from a relay test",
    reaction: "none",
  };
  return encryptPayload(activeRecipientPublicKey, payload, {
    messageId: overrides.messageId,
    createdUtc: overrides.createdUtc,
    deliverAfterUtc: overrides.deliverAfterUtc,
  });
}

/** Reads the stored ciphertext column straight through `env.DB`; null once acked/expired/absent. */
export async function messageCiphertext(messageId: string): Promise<string | null> {
  const testEnv = env as unknown as { DB: D1Database };
  const row = await testEnv.DB.prepare(`SELECT ciphertext FROM messages WHERE id = ?1`)
    .bind(messageId)
    .first<{ ciphertext: string }>();
  return row?.ciphertext ?? null;
}

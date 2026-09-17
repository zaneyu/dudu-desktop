import { exports } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { router } from "../src/index.js";
import { bytesToBase64Url } from "../src/security/tokens.js";
import { fetchWorker, jsonHeaders, pairedFixture, validEnvelope, type PairedFixture } from "./helpers.js";

const BASE_URL = "https://example.test";

// Ruling #4 (task-21-review-1.md, Important) asks for header coverage on a 500 path, not just
// 200/404. `router.handle`'s catch-all only fires when a matched route handler throws, so this
// test-only route (registered once, at module load, on the same `Router` instance `index.ts`'s
// `fetch` entry point uses) exists purely to make that throw happen deterministically, without
// depending on any production route ever misbehaving.
router.add("GET", "/v1/__test-throws-500", () => {
  throw new Error("deliberate failure for the security-header 500-path test");
});

/**
 * One paired fixture shared by every row below. Each row needs a VALID sender session so the
 * defense under test (origin, content type, envelope shape, ciphertext size) is what rejects the
 * request — not an unrelated 401/403 from a missing session.
 */
const fixture: PairedFixture = await pairedFixture();

const BINDING_HEADERS = {
  "X-Dudu-Device": fixture.deviceId,
  "X-Dudu-Recipient-Key": fixture.recipientPublicKey,
};

function requestWithOrigin(origin: string): Request {
  return new Request(`${BASE_URL}/v1/messages`, {
    method: "POST",
    headers: jsonHeaders({ Origin: origin, Cookie: fixture.sessionCookie, ...BINDING_HEADERS }),
    body: JSON.stringify({}),
  });
}

function requestWithContentType(contentType: string): Request {
  return new Request(`${BASE_URL}/v1/messages`, {
    method: "POST",
    headers: new Headers({
      "Content-Type": contentType,
      Origin: BASE_URL,
      Cookie: fixture.sessionCookie,
      ...BINDING_HEADERS,
    }),
    body: "not json",
  });
}

function requestWithEnvelope(body: unknown): Request {
  return new Request(`${BASE_URL}/v1/messages`, {
    method: "POST",
    headers: jsonHeaders({ Origin: BASE_URL, Cookie: fixture.sessionCookie, ...BINDING_HEADERS }),
    body: JSON.stringify(body),
  });
}

/**
 * `requestWithCiphertext(...)` is asynchronous (it encrypts a real envelope so the relay's
 * earlier-running checks — origin, session, content type, envelope shape — all pass and only the
 * ciphertext-size bound is exercised), so the brief's snippet is awaited below; everything else
 * matches it verbatim. Mirrors the precedent in `messages.spec.ts` for the same situation.
 */
async function requestWithCiphertext(oversizedByteLength: number): Promise<Request> {
  const envelope = await validEnvelope({ messageId: crypto.randomUUID() });
  const oversized = { ...envelope, ciphertext: bytesToBase64Url(new Uint8Array(oversizedByteLength)) };
  return new Request(`${BASE_URL}/v1/messages`, {
    method: "POST",
    headers: jsonHeaders({ Origin: BASE_URL, Cookie: fixture.sessionCookie, ...BINDING_HEADERS }),
    body: JSON.stringify(oversized),
  });
}

// `it.each`'s array literal cannot itself contain an `await` (it runs inside `describe`'s
// synchronous callback), so the one async row's request is built here, at module level, via
// top-level await -- the same technique `helpers.ts` already uses for `TEST_PUBLIC_KEY`. The
// `it.each` call immediately below is otherwise verbatim from the Task 21 brief (Step 1).
const oversizedCiphertextRequest = await requestWithCiphertext(6145);

describe("Worker security hardening", () => {
  // --- The verbatim it.each from the Task 21 brief (Step 1). ---

  it.each([
    ["wrong origin", requestWithOrigin("https://evil.example"), 403],
    ["wrong content type", requestWithContentType("text/plain"), 415],
    ["unknown envelope key", requestWithEnvelope({ extra: true }), 422],
    ["oversized ciphertext", oversizedCiphertextRequest, 413],
  ])("rejects %s", async (_name, request, status) => {
    expect((await exports.default.fetch(request)).status).toBe(status);
  });

  // --- Additional coverage for the rest of the controller's rulings. ---

  it("rejects a JSON body with duplicate top-level keys, not the second value silently winning", async () => {
    const response = await exports.default.fetch(
      new Request(`${BASE_URL}/v1/messages`, {
        method: "POST",
        headers: jsonHeaders({ Origin: BASE_URL, Cookie: fixture.sessionCookie, ...BINDING_HEADERS }),
        body: '{"protocolVersion":1,"protocolVersion":1,"messageId":"00000000-0000-4000-8000-000000000000"}',
      }),
    );
    expect(response.status).toBe(400);
  });

  it("rejects a JSON body carrying a __proto__ key", async () => {
    const response = await exports.default.fetch(
      new Request(`${BASE_URL}/v1/messages`, {
        method: "POST",
        headers: jsonHeaders({ Origin: BASE_URL, Cookie: fixture.sessionCookie, ...BINDING_HEADERS }),
        body: '{"__proto__":{"polluted":true}}',
      }),
    );
    expect(response.status).toBe(400);
  });

  it("rejects a raw body far larger than any legitimate request, before JSON.parse ever runs", async () => {
    const response = await exports.default.fetch(
      new Request(`${BASE_URL}/v1/messages`, {
        method: "POST",
        headers: jsonHeaders({ Origin: BASE_URL, Cookie: fixture.sessionCookie, ...BINDING_HEADERS }),
        body: "x".repeat(1024 * 1024),
      }),
    );
    expect(response.status).toBe(413);
  });

  it("rejects noncanonical Base64URL ciphertext with 422", async () => {
    const envelope = await validEnvelope({ messageId: crypto.randomUUID() });

    // 17 zero bytes (the wire contract's minimum ciphertext length) encode, canonically, to a
    // Base64URL string whose last character is "A" (alphabet index 0 -- every bit, real and
    // slack alike, is zero). Substituting "B" (alphabet index 1) sets only the two unused
    // "slack" bits of that character to a nonzero value: decoding still drops those bits and
    // yields the same 17 zero bytes, so the canonical re-encoding comes back as "A" again --
    // not "B". That mismatch is the textbook definition of "noncanonical", constructed
    // deterministically rather than by perturbing whichever ciphertext validEnvelope() happens
    // to produce (a blind last-character flip can just as easily land on a *real* data bit,
    // which changes the decoded bytes without becoming noncanonical at all).
    const canonical = bytesToBase64Url(new Uint8Array(17));
    expect(canonical.endsWith("A")).toBe(true);
    const noncanonical = `${canonical.slice(0, -1)}B`;

    const response = await exports.default.fetch(
      new Request(`${BASE_URL}/v1/messages`, {
        method: "POST",
        headers: jsonHeaders({ Origin: BASE_URL, Cookie: fixture.sessionCookie, ...BINDING_HEADERS }),
        body: JSON.stringify({ ...envelope, ciphertext: noncanonical }),
      }),
    );
    expect(response.status).toBe(422);
  });

  it("sets CSP, nosniff, no-referrer, permissions-policy, and no-store on a successful response", async () => {
    const response = await fetchWorker("/v1/messages", {
      headers: { Authorization: `Bearer ${fixture.desktopToken}` },
    });

    expect(response.status).toBe(200);
    expect(response.headers.get("Content-Security-Policy")).toBeTruthy();
    expect(response.headers.get("X-Content-Type-Options")).toBe("nosniff");
    expect(response.headers.get("Referrer-Policy")).toBe("no-referrer");
    expect(response.headers.get("Permissions-Policy")).toBe(
      "camera=(), microphone=(), geolocation=()",
    );
    expect(response.headers.get("Cache-Control")).toBe("no-store");
  });

  it("sets the same security headers on a 404", async () => {
    const response = await fetchWorker("/v1/no-such-route");

    expect(response.status).toBe(404);
    expect(response.headers.get("Content-Security-Policy")).toBeTruthy();
    expect(response.headers.get("X-Content-Type-Options")).toBe("nosniff");
    expect(response.headers.get("Referrer-Policy")).toBe("no-referrer");
  });

  it("sets the same security headers on a 500 from an unhandled route-handler error", async () => {
    const response = await fetchWorker("/v1/__test-throws-500");

    expect(response.status).toBe(500);
    expect(response.headers.get("Content-Security-Policy")).toBeTruthy();
    expect(response.headers.get("X-Content-Type-Options")).toBe("nosniff");
    expect(response.headers.get("Referrer-Policy")).toBe("no-referrer");
    expect(response.headers.get("Permissions-Policy")).toBe(
      "camera=(), microphone=(), geolocation=()",
    );
    expect(response.headers.get("Cache-Control")).toBe("no-store");
  });
});

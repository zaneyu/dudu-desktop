/**
 * Intercepts every `/v1/*` call a Playwright test page makes and answers with an in-memory
 * mock of the relay. Generates a real P-256 recipient key pair with Node's `crypto.subtle` (the
 * same Web Crypto API the browser page uses), so a test can decrypt whatever the page actually
 * sent and prove no plaintext note text ever left the browser.
 */
import type { Page } from "@playwright/test";

import { createRecipientForTest, decryptPayloadForTest } from "../sender-src/crypto.js";
import type { EncryptedEnvelopeV1, MessageState, RemoteMessagePayloadV1 } from "../src/protocol/types.js";

export interface MockRelay {
  /** The exact JSON body string of the most recent `POST /v1/messages` request, if any. */
  lastRequestBody: string | null;
  /** What the intercepted ciphertext actually decrypts to, using the mock recipient's private
   * key. Null until a message has been posted, or if decryption/validation failed. */
  lastDecryptedPayload: RemoteMessagePayloadV1 | null;
  /** Whether the mock currently considers this browser paired. Toggled by redeem/disconnect. */
  paired: boolean;
  /** messageId -> status, seeded by each accepted `POST /v1/messages`. */
  statuses: Map<string, MessageState>;
  /** When set, the NEXT `POST /v1/messages` fails this way instead of succeeding, then the flag
   * clears itself: `"abort"` simulates a dropped connection (Playwright `route.abort()`, so the
   * page's `fetch` rejects and the composer sees `ApiNetworkError`), or an HTTP status number
   * (e.g. 500, 401, 413, 429) fulfills the response with that status and a generic error body. */
  failNextSend: "abort" | number | null;
}

const VALID_PAIRING_CODE = "7K9M2R4X";

async function exportSpkiBase64Url(publicKey: CryptoKey): Promise<string> {
  const raw = await crypto.subtle.exportKey("spki", publicKey);
  return Buffer.from(raw).toString("base64url");
}

export async function mockRelay(page: Page): Promise<MockRelay> {
  const recipient = await createRecipientForTest();
  const publicKeySpki = await exportSpkiBase64Url(recipient.publicKey);
  const deviceId = "mock-device-1";

  const state: MockRelay = {
    lastRequestBody: null,
    lastDecryptedPayload: null,
    paired: false,
    statuses: new Map(),
    failNextSend: null,
  };

  await page.route("**/v1/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();

    if (method === "POST" && url.pathname === "/v1/pairings/redeem") {
      let code: unknown;
      try {
        code = (JSON.parse(request.postData() ?? "{}") as { code?: unknown }).code;
      } catch {
        code = null;
      }
      if (code === VALID_PAIRING_CODE) {
        state.paired = true;
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({ publicKey: publicKeySpki, deviceId }),
        });
      } else {
        await route.fulfill({
          status: 410,
          contentType: "application/json",
          body: JSON.stringify({ error: "gone", message: "This pairing code is no longer valid." }),
        });
      }
      return;
    }

    if (method === "GET" && url.pathname === "/v1/sender/device") {
      if (state.paired) {
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            publicKey: publicKeySpki,
            deviceCreatedUtc: new Date().toISOString(),
            publicKeyFingerprint: "mock-fingerprint",
          }),
        });
      } else {
        await route.fulfill({
          status: 401,
          contentType: "application/json",
          body: JSON.stringify({ error: "unauthorized", message: "Authentication is required." }),
        });
      }
      return;
    }

    if (method === "POST" && url.pathname === "/v1/sender/disconnect") {
      state.paired = false;
      await route.fulfill({ status: 204, body: "" });
      return;
    }

    if (method === "POST" && url.pathname === "/v1/messages") {
      if (state.failNextSend !== null) {
        const failure = state.failNextSend;
        state.failNextSend = null;
        if (failure === "abort") {
          await route.abort("failed");
        } else {
          await route.fulfill({
            status: failure,
            contentType: "application/json",
            body: JSON.stringify({ error: "mock_failure", message: "Simulated failure for a test." }),
          });
        }
        return;
      }

      const bodyText = request.postData() ?? "";
      state.lastRequestBody = bodyText;
      let envelope: EncryptedEnvelopeV1 | null = null;
      try {
        envelope = JSON.parse(bodyText) as EncryptedEnvelopeV1;
      } catch {
        envelope = null;
      }
      if (!envelope) {
        await route.fulfill({
          status: 400,
          contentType: "application/json",
          body: JSON.stringify({ error: "bad_request", message: "The request body is malformed." }),
        });
        return;
      }
      try {
        state.lastDecryptedPayload = await decryptPayloadForTest(recipient.privateKey, envelope);
      } catch {
        state.lastDecryptedPayload = null;
      }
      state.statuses.set(envelope.messageId, "queued");
      await route.fulfill({
        status: 202,
        contentType: "application/json",
        body: JSON.stringify({ messageId: envelope.messageId, status: "queued" }),
      });
      return;
    }

    const statusMatch = /^\/v1\/messages\/([^/]+)\/status$/.exec(url.pathname);
    if (method === "GET" && statusMatch) {
      const status = state.statuses.get(statusMatch[1]);
      if (!status) {
        await route.fulfill({
          status: 404,
          contentType: "application/json",
          body: JSON.stringify({ error: "not_found", message: "No route matches this request." }),
        });
      } else {
        await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ status }) });
      }
      return;
    }

    await route.fulfill({
      status: 404,
      contentType: "application/json",
      body: JSON.stringify({ error: "not_found", message: "No route matches this request." }),
    });
  });

  return state;
}

export { VALID_PAIRING_CODE };

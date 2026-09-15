/**
 * Node-environment unit tests for the sender page's logic that has no business being driven
 * through a browser: the draft-id lifecycle around a 401 (review I5) and the trust-on-first-use
 * key pin in `verifyStoredSession` (review I6). The composer needs only a handful of element
 * properties, so this file stubs them rather than pulling in a DOM implementation; the real
 * page is covered end-to-end by `e2e/`.
 */
import { beforeEach, describe, expect, it } from "vitest";
import { MessageComposer, type ComposerElements } from "../sender-src/composer.js";
import { createRecipientForTest } from "../sender-src/crypto.js";
import {
  clearStoredDevice,
  disconnect,
  loadStoredDevice,
  pairWithCode,
  verifyStoredSession,
} from "../sender-src/pairing.js";
import type { EncryptedEnvelopeV1 } from "../src/protocol/types.js";
import { bytesToBase64Url } from "../src/security/tokens.js";

const RECIPIENT = await createRecipientForTest();
const RECIPIENT_PUBLIC_KEY = bytesToBase64Url(
  new Uint8Array(await crypto.subtle.exportKey("spki", RECIPIENT.publicKey)),
);
const OTHER_PUBLIC_KEY = bytesToBase64Url(
  new Uint8Array(await crypto.subtle.exportKey("spki", (await createRecipientForTest()).publicKey)),
);

/** The two `localStorage` members `pairing.ts` uses, backed by a plain Map. */
function installLocalStorage(): void {
  const entries = new Map<string, string>();
  Object.defineProperty(globalThis, "localStorage", {
    configurable: true,
    value: {
      getItem: (key: string) => entries.get(key) ?? null,
      setItem: (key: string, value: string) => void entries.set(key, value),
      removeItem: (key: string) => void entries.delete(key),
    },
  });
}

type FetchHandler = (url: string, init?: RequestInit) => Response;

function installFetch(handler: FetchHandler): void {
  Object.defineProperty(globalThis, "fetch", {
    configurable: true,
    writable: true,
    value: (input: string, init?: RequestInit) => Promise.resolve(handler(String(input), init)),
  });
}

function jsonOk(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

class FakeElement {
  readonly listeners = new Map<string, Array<(event: unknown) => void>>();
  textContent = "";
  value = "";
  checked = false;
  disabled = false;

  addEventListener(type: string, handler: (event: unknown) => void): void {
    const existing = this.listeners.get(type) ?? [];
    existing.push(handler);
    this.listeners.set(type, existing);
  }

  dispatch(type: string, event: unknown = { preventDefault: () => undefined }): void {
    for (const handler of this.listeners.get(type) ?? []) {
      handler(event);
    }
  }

  show(): void {}
  close(): void {}
}

interface ComposerHarness {
  composer: MessageComposer;
  elements: Record<string, FakeElement>;
  unauthorizedCount: () => number;
}

function buildComposer(): ComposerHarness {
  const named = [
    "form",
    "textArea",
    "counter",
    "sendLaterInput",
    "scheduleStatus",
    "previewButton",
    "previewDialog",
    "previewText",
    "previewReaction",
    "previewClose",
    "sendButton",
    "sendStatus",
  ];
  const elements: Record<string, FakeElement> = {};
  for (const name of named) {
    elements[name] = new FakeElement();
  }
  let unauthorized = 0;
  const composer = new MessageComposer(
    { ...elements, reactionInputs: [] } as unknown as ComposerElements,
    { onUnauthorized: () => void (unauthorized += 1) },
  );
  composer.setRecipientPublicKey(RECIPIENT_PUBLIC_KEY);
  return { composer, elements, unauthorizedCount: () => unauthorized };
}

async function waitFor(condition: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    if (condition()) {
      return;
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error("waitFor: condition never became true.");
}

describe("sender pairing key pinning", () => {
  beforeEach(() => {
    installLocalStorage();
  });

  it("keeps the session when the relay still reports the key seen at pairing", async () => {
    const fingerprint = await sha256HexForPublicKey(RECIPIENT_PUBLIC_KEY);
    installFetch(() => jsonOk({ deviceId: "device-1", publicKey: RECIPIENT_PUBLIC_KEY, publicKeyFingerprint: fingerprint }));
    await pairWithCode("123456");

    const result = await verifyStoredSession();

    expect(result).toEqual({
      state: "paired",
      device: { deviceId: "device-1", publicKey: RECIPIENT_PUBLIC_KEY, publicKeyFingerprint: fingerprint },
    });
    expect(loadStoredDevice()).not.toBeNull();
  });

  it("drops the session when the relay reports a different key than the pinned one", async () => {
    // Review I6: without this pin the relay could hand the sender its own key and read every
    // note. The sender trusts the key it saw at pairing and refuses a silent swap.
    const fingerprint = await sha256HexForPublicKey(RECIPIENT_PUBLIC_KEY);
    installFetch(() => jsonOk({ deviceId: "device-1", publicKey: RECIPIENT_PUBLIC_KEY, publicKeyFingerprint: fingerprint }));
    await pairWithCode("123456");
    installFetch(() => jsonOk({ deviceId: "device-1", publicKey: OTHER_PUBLIC_KEY, publicKeyFingerprint: fingerprint }));

    const result = await verifyStoredSession();

    expect(result).toEqual({ state: "key-changed" });
    expect(loadStoredDevice()).toBeNull();
  });

  it("reports an unpaired state and clears the cache when the relay says the session is gone", async () => {
    const fingerprint = await sha256HexForPublicKey(RECIPIENT_PUBLIC_KEY);
    installFetch(() => jsonOk({ deviceId: "device-1", publicKey: RECIPIENT_PUBLIC_KEY, publicKeyFingerprint: fingerprint }));
    await pairWithCode("123456");
    installFetch(() => new Response(null, { status: 401 }));

    expect(await verifyStoredSession()).toEqual({ state: "unpaired" });
    expect(loadStoredDevice()).toBeNull();
  });

  it("reports an unpaired state when nothing is stored", async () => {
    clearStoredDevice();
    installFetch(() => {
      throw new Error("verifyStoredSession must not call the relay with no stored device.");
    });

    expect(await verifyStoredSession()).toEqual({ state: "unpaired" });
  });

  it("rejects an initial pairing response whose fingerprint does not match its public key", async () => {
    installFetch(() =>
      jsonOk({ deviceId: "device-1", publicKey: RECIPIENT_PUBLIC_KEY, publicKeyFingerprint: "00".repeat(32) }),
    );

    await expect(pairWithCode("123456")).rejects.toThrow("mismatched device fingerprint");
    expect(loadStoredDevice()).toBeNull();
  });
});

describe("sender disconnect", () => {
  beforeEach(() => {
    installLocalStorage();
  });

  it("retains the paired state when the revoke request cannot reach the relay", async () => {
    const fingerprint = await sha256HexForPublicKey(RECIPIENT_PUBLIC_KEY);
    installFetch(() => jsonOk({ deviceId: "device-1", publicKey: RECIPIENT_PUBLIC_KEY, publicKeyFingerprint: fingerprint }));
    await pairWithCode("123456");
    installFetch(() => {
      throw new Error("offline");
    });

    await expect(disconnect()).rejects.toThrow("Could not reach the relay");
    expect(loadStoredDevice()).not.toBeNull();
  });

  it("retains the paired state unless the relay returns the revocation confirmation", async () => {
    const fingerprint = await sha256HexForPublicKey(RECIPIENT_PUBLIC_KEY);
    installFetch(() => jsonOk({ deviceId: "device-1", publicKey: RECIPIENT_PUBLIC_KEY, publicKeyFingerprint: fingerprint }));
    await pairWithCode("123456");
    installFetch(() => jsonOk({ error: "internal", message: "try again" }, 500));

    await expect(disconnect()).rejects.toThrow("try again");
    expect(loadStoredDevice()).not.toBeNull();

    installFetch(() => new Response(null, { status: 204 }));
    await disconnect();
    expect(loadStoredDevice()).toBeNull();
  });
});

describe("composer draft id", () => {
  beforeEach(() => {
    installLocalStorage();
  });

  it("mints a fresh message id for the send that follows a 401", async () => {
    // Review I5: the draft id survived the unauthorized branch, so the first send after
    // re-pairing reused it -- and the relay's (sender, messageId) dedup answered 409 for a note
    // the new session had never sent, leaving the note permanently unsendable.
    const posted: EncryptedEnvelopeV1[] = [];
    let sendStatus = 401;
    installFetch((url, init) => {
      if (url === "/v1/messages") {
        posted.push(JSON.parse(String(init?.body)) as EncryptedEnvelopeV1);
        return sendStatus === 401
          ? new Response(null, { status: 401 })
          : jsonOk({ messageId: posted[posted.length - 1].messageId, status: "queued" }, 202);
      }
      throw new Error(`Unexpected request to ${url}`);
    });
    const harness = buildComposer();
    harness.elements.textArea.value = "same note, unchanged";

    harness.elements.form.dispatch("submit");
    await waitFor(() => posted.length === 1);
    expect(harness.unauthorizedCount()).toBe(1);

    sendStatus = 202;
    harness.elements.form.dispatch("submit");
    await waitFor(() => posted.length === 2);

    expect(posted[1].messageId).not.toBe(posted[0].messageId);
  });

  it("rejects an empty or whitespace-only note before encrypting or posting", async () => {
    installFetch(() => {
      throw new Error("An empty note must not reach the relay.");
    });
    const harness = buildComposer();
    harness.elements.textArea.value = " \n  ";

    harness.elements.form.dispatch("submit");

    expect(harness.elements.sendStatus.textContent).toBe("write something first");
  });

  it("reuses the message id for an identical retry that was not a 401", async () => {
    const posted: EncryptedEnvelopeV1[] = [];
    let status = 500;
    installFetch((url, init) => {
      if (url === "/v1/messages") {
        posted.push(JSON.parse(String(init?.body)) as EncryptedEnvelopeV1);
        return status === 500
          ? jsonOk({ code: "internal", message: "nope" }, 500)
          : jsonOk({ messageId: posted[posted.length - 1].messageId, status: "queued" }, 202);
      }
      throw new Error(`Unexpected request to ${url}`);
    });
    const harness = buildComposer();
    harness.elements.textArea.value = "same note, unchanged";

    harness.elements.form.dispatch("submit");
    await waitFor(() => posted.length === 1);
    status = 202;
    harness.elements.form.dispatch("submit");
    await waitFor(() => posted.length === 2);

    expect(posted[1].messageId).toBe(posted[0].messageId);
  });
});

async function sha256HexForPublicKey(publicKey: string): Promise<string> {
  const binary = atob(publicKey.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((publicKey.length + 3) % 4));
  const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
}

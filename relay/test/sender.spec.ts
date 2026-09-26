/**
 * Node-environment unit tests for the sender page's logic that has no business being driven
 * through a browser: the draft-id lifecycle around a 401 (review I5) and the trust-on-first-use
 * key pin in `verifyStoredSession` (review I6). The composer needs only a handful of element
 * properties, so this file stubs them rather than pulling in a DOM implementation; the real
 * page is covered end-to-end by `e2e/`.
 */
import { beforeEach, describe, expect, it, vi } from "vitest";
import { addRecentStatus, describeRecentStatus, StatusTracker } from "../sender-src/status.js";
import {
  measureNote,
  MessageComposer,
  type ComposerElements,
  type ComposerSessionLoss,
} from "../sender-src/composer.js";
import { createRecipientForTest } from "../sender-src/crypto.js";
import { redeemPairing } from "../sender-src/api.js";
import {
  clearStoredDevice,
  disconnect,
  isWellFormedPairingCode,
  loadStoredDevice,
  normalizePairingCodeTyping,
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
  focus(): void {}
  setAttribute(): void {}
  removeAttribute(): void {}
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
  composer.setRecipientPublicKey(RECIPIENT_PUBLIC_KEY, "device-1");
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
    harness.composer.setRecipientPublicKey(RECIPIENT_PUBLIC_KEY, "device-1");
    harness.elements.textArea.value = "same note, unchanged";
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

  it("resends the exact encrypted envelope for an unchanged retry", async () => {
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

    expect(posted[1]).toEqual(posted[0]);
  });
});

describe("sender status polling", () => {
  it("stops polling when a queued message status is no longer available", async () => {
    vi.useFakeTimers();
    const entries = new Map<string, string>();
    vi.stubGlobal("sessionStorage", {
      getItem: (key: string) => entries.get(key) ?? null,
      setItem: (key: string, value: string) => entries.set(key, value),
      removeItem: (key: string) => entries.delete(key),
    });
    vi.stubGlobal("document", {
      visibilityState: "visible",
      createElement: () => ({ textContent: "" }),
    });
    const fetchStatus = vi.fn(async () => new Response(null, { status: 404 }));
    vi.stubGlobal("fetch", fetchStatus);
    const replaceChildren = vi.fn();
    const tracker = new StatusTracker({ replaceChildren } as unknown as HTMLElement, vi.fn());
    try {
      addRecentStatus("test-message", "queued");
      tracker.start();
      await vi.advanceTimersByTimeAsync(30_000);
      expect(fetchStatus).toHaveBeenCalledTimes(1);
      expect(replaceChildren).toHaveBeenLastCalledWith({ textContent: "status no longer available" });
      await vi.advanceTimersByTimeAsync(60_000);
      expect(fetchStatus).toHaveBeenCalledTimes(1);
    } finally {
      tracker.stop();
      vi.useRealTimers();
      vi.unstubAllGlobals();
    }
  });
});

// ---------------------------------------------------------------------------------------------
// Sender UI/UX regressions (pairing input cleanup, counter accuracy, stale status lines, session
// loss reasons, retry after validation failure, request timeouts).
// ---------------------------------------------------------------------------------------------

describe("pairing code input cleanup", () => {
  it("strips separators and whitespace from a pasted code and upper-cases it", () => {
    expect(normalizePairingCodeTyping(" 7k9m-2r4x ")).toBe("7K9M2R4X");
    expect(normalizePairingCodeTyping("7K9M 2R4X")).toBe("7K9M2R4X");
    expect(normalizePairingCodeTyping("7K9M\u00a02R4X\n")).toBe("7K9M2R4X");
  });

  it("maps the Crockford look-alikes the code alphabet omits", () => {
    expect(normalizePairingCodeTyping("OIL00111")).toBe("01100111");
    expect(isWellFormedPairingCode(normalizePairingCodeTyping("7k9m2r4o"))).toBe(true);
  });

  it("caps the cleaned code at eight characters", () => {
    expect(normalizePairingCodeTyping("7K9M2R4XZZ")).toBe("7K9M2R4X");
  });

  it("rejects short or out-of-alphabet codes before they reach the relay", () => {
    expect(isWellFormedPairingCode("")).toBe(false);
    expect(isWellFormedPairingCode("7K9M2R4")).toBe(false);
    expect(isWellFormedPairingCode(normalizePairingCodeTyping("7K9M2R4U"))).toBe(false);
    expect(isWellFormedPairingCode("7K9M2R4X")).toBe(true);
  });
});

describe("note measurement", () => {
  it("counts emoji and other astral characters as one scalar value each", () => {
    expect(measureNote("😀😀", "none").scalarValues).toBe(2);
  });

  it("flags a note that fits 2000 characters but not the encrypted payload's byte budget", () => {
    // 1500 CJK characters are 4500 UTF-8 bytes: under the character limit, over 4096 bytes.
    const measurement = measureNote("爱".repeat(1500), "none");
    expect(measurement.scalarValues).toBe(1500);
    expect(measurement.tooLong).toBe(true);
  });

  it("agrees with TextEncoder on the payload byte size, including escapes and lone surrogates", () => {
    const text = 'quote " backslash \\ newline \n tab \t emoji 😀 lone \ud800';
    const expected = new TextEncoder().encode(JSON.stringify({ kind: "note", text, reaction: "celebrate" })).byteLength;
    expect(measureNote(text, "celebrate").payloadBytes).toBe(expected);
  });

  it("does not flag a 2000-character ASCII note", () => {
    expect(measureNote("a".repeat(2000), "celebrate").tooLong).toBe(false);
    expect(measureNote("a".repeat(2001), "none").tooLong).toBe(true);
  });
});

class UxFakeElement extends FakeElement {
  readonly attributes = new Map<string, string>();
  min = "";
  max = "";
  focusCount = 0;
  validity = { badInput: false };

  override setAttribute(name?: string, value?: string): void {
    this.attributes.set(String(name), String(value ?? ""));
  }

  override removeAttribute(name?: string): void {
    this.attributes.delete(String(name));
  }

  override focus(): void {
    this.focusCount += 1;
  }
}

interface UxHarness {
  elements: Record<string, UxFakeElement>;
  losses: ComposerSessionLoss[];
  sent: () => number;
  composer: MessageComposer;
}

function buildUxComposer(): UxHarness {
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
  const elements: Record<string, UxFakeElement> = {};
  for (const name of named) {
    elements[name] = new UxFakeElement();
  }
  const losses: ComposerSessionLoss[] = [];
  let sent = 0;
  const composer = new MessageComposer({ ...elements, reactionInputs: [] } as unknown as ComposerElements, {
    onUnauthorized: (reason) => void losses.push(reason),
    onSent: () => void (sent += 1),
  });
  composer.setRecipientPublicKey(RECIPIENT_PUBLIC_KEY, "device-1");
  return { elements, losses, sent: () => sent, composer };
}

function installSessionStorage(): void {
  const entries = new Map<string, string>();
  Object.defineProperty(globalThis, "sessionStorage", {
    configurable: true,
    value: {
      getItem: (key: string) => entries.get(key) ?? null,
      setItem: (key: string, value: string) => void entries.set(key, value),
      removeItem: (key: string) => void entries.delete(key),
    },
  });
}

describe("composer counter and status lines", () => {
  beforeEach(() => {
    installLocalStorage();
    installSessionStorage();
  });

  it("marks the counter and field over limit in words, not only color", () => {
    const { elements } = buildUxComposer();
    elements.textArea.value = "爱".repeat(1500);
    elements.textArea.dispatch("input");

    expect(elements.counter.textContent).toBe("1500 / 2000 aiyo too long trim it abit");
    expect(elements.counter.attributes.has("data-over-limit")).toBe(true);
    expect(elements.textArea.attributes.get("aria-invalid")).toBe("true");

    elements.textArea.value = "short again";
    elements.textArea.dispatch("input");
    expect(elements.counter.textContent).toBe("11 / 2000");
    expect(elements.counter.attributes.has("data-over-limit")).toBe(false);
    expect(elements.textArea.attributes.has("aria-invalid")).toBe(false);
  });

  it("counts an emoji note in characters, matching the send-time limit", () => {
    const { elements } = buildUxComposer();
    elements.textArea.value = "😀".repeat(1001);
    elements.textArea.dispatch("input");
    // 1001 emoji are 2002 UTF-16 units: a textarea maxlength would have cut this at 1000.
    expect(elements.counter.textContent).toBe("1001 / 2000");
  });

  it("clears the previous result line when the next note is started", async () => {
    installFetch((url, init) =>
      url === "/v1/messages"
        ? jsonOk({ messageId: (JSON.parse(String(init?.body)) as EncryptedEnvelopeV1).messageId, status: "queued" }, 202)
        : new Response(null, { status: 404 }),
    );
    const { elements, sent } = buildUxComposer();
    elements.textArea.value = "first note";
    elements.form.dispatch("submit");
    await waitFor(() => elements.sendStatus.textContent === "Queued securely");
    expect(sent()).toBe(1);

    elements.textArea.value = "s";
    elements.textArea.dispatch("input");
    expect(elements.sendStatus.textContent).toBe("");
  });

  it("clears a stale schedule error once a different time is picked", () => {
    const { elements } = buildUxComposer();
    elements.textArea.value = "later";
    elements.sendLaterInput.value = "2000-01-01T00:00";
    elements.form.dispatch("submit");
    expect(elements.scheduleStatus.textContent).toBe("choose a future time");

    elements.sendLaterInput.value = "2999-01-01T00:00";
    elements.sendLaterInput.dispatch("input");
    expect(elements.scheduleStatus.textContent).toBe("");
  });

  it("refuses to send immediately while the schedule picker is half filled", () => {
    installFetch(() => {
      throw new Error("A half-picked schedule must not send the note right away.");
    });
    const { elements } = buildUxComposer();
    elements.textArea.value = "for later";
    elements.sendLaterInput.value = "";
    elements.sendLaterInput.validity.badInput = true;
    elements.form.dispatch("submit");
    expect(elements.scheduleStatus.textContent).toBe("finish picking the time or clear it");
  });

  it("bounds the schedule picker to now through the retention window", () => {
    const { elements } = buildUxComposer();
    elements.sendLaterInput.dispatch("focus");
    expect(elements.sendLaterInput.min).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);
    expect(elements.sendLaterInput.max > elements.sendLaterInput.min).toBe(true);
  });

  it("confirms a scheduled note with its local delivery time", async () => {
    installFetch((url, init) =>
      url === "/v1/messages"
        ? jsonOk({ messageId: (JSON.parse(String(init?.body)) as EncryptedEnvelopeV1).messageId, status: "queued" }, 202)
        : new Response(null, { status: 404 }),
    );
    const { elements } = buildUxComposer();
    const future = new Date(Date.now() + 2 * 86400_000);
    const pad = (value: number) => String(value).padStart(2, "0");
    elements.textArea.value = "see you soon";
    elements.sendLaterInput.value =
      `${future.getFullYear()}-${pad(future.getMonth() + 1)}-${pad(future.getDate())}T${pad(future.getHours())}:${pad(future.getMinutes())}`;
    elements.form.dispatch("submit");
    await waitFor(() => elements.sendStatus.textContent.startsWith("Scheduled securely for "));
  });
});

describe("composer session loss and retry", () => {
  beforeEach(() => {
    installLocalStorage();
    installSessionStorage();
  });

  it("reports a recipient key change (412) distinctly from a lost session (401)", async () => {
    let status = 412;
    installFetch((url) =>
      url === "/v1/messages"
        ? jsonOk({ error: "recipient_changed", message: "Confirm the recipient before sending." }, status)
        : new Response(null, { status: 404 }),
    );
    const harness = buildUxComposer();
    harness.elements.textArea.value = "hello";
    harness.elements.form.dispatch("submit");
    await waitFor(() => harness.losses.length === 1);
    expect(harness.losses[0]).toBe("recipient-changed");

    status = 401;
    harness.composer.setRecipientPublicKey(RECIPIENT_PUBLIC_KEY, "device-1");
    harness.elements.textArea.value = "hello";
    harness.elements.form.dispatch("submit");
    await waitFor(() => harness.losses.length === 2);
    expect(harness.losses[1]).toBe("session-lost");
  });

  it("re-encrypts after the relay rejects the cached envelope with 422 so a retry can succeed", async () => {
    const posted: EncryptedEnvelopeV1[] = [];
    let status = 422;
    installFetch((url, init) => {
      if (url === "/v1/messages") {
        posted.push(JSON.parse(String(init?.body)) as EncryptedEnvelopeV1);
        return status === 422
          ? jsonOk({ error: "unprocessable", message: "createdUtc outside window" }, 422)
          : jsonOk({ messageId: posted[posted.length - 1].messageId, status: "queued" }, 202);
      }
      throw new Error(`Unexpected request to ${url}`);
    });
    const { elements } = buildUxComposer();
    elements.textArea.value = "unchanged note";
    elements.form.dispatch("submit");
    await waitFor(() => elements.sendStatus.textContent === "aiyo couldnt send try again");

    status = 202;
    elements.form.dispatch("submit");
    await waitFor(() => elements.sendStatus.textContent === "Queued securely");
    expect(posted).toHaveLength(2);
    expect(posted[1].messageId).not.toBe(posted[0].messageId);
    expect(posted[1].createdUtc >= posted[0].createdUtc).toBe(true);
  });
});

describe("recent status lines", () => {
  it("keeps the bare status for entries without timing details", () => {
    expect(describeRecentStatus({ messageId: "a", status: "queued" })).toBe("on the way");
  });

  it("says when each note was sent and, if scheduled, when it is due", () => {
    const sentUtc = "2026-01-02T03:04:00.000Z";
    const deliverAfterUtc = "2026-01-05T20:30:00.000Z";
    const line = describeRecentStatus({ messageId: "a", status: "queued", sentUtc, deliverAfterUtc });
    const sentLocal = new Date(sentUtc).toLocaleString(undefined, { hour: "numeric", minute: "2-digit" });
    expect(line.startsWith(`sent ${sentLocal} for `)).toBe(true);
    expect(line.endsWith(" · on the way")).toBe(true);
  });
});

describe("sender request timeouts", () => {
  it("turns a pairing request that never answers into a network error instead of hanging", async () => {
    vi.useFakeTimers();
    vi.stubGlobal(
      "fetch",
      (_url: string, init?: RequestInit) =>
        new Promise((_resolve, reject) => {
          init?.signal?.addEventListener("abort", () => reject(new Error("aborted")));
        }),
    );
    try {
      const pending = redeemPairing("7K9M2R4X");
      const assertion = expect(pending).rejects.toThrow("Could not reach the relay.");
      await vi.advanceTimersByTimeAsync(15_000);
      await assertion;
    } finally {
      vi.useRealTimers();
      vi.unstubAllGlobals();
    }
  });
});

async function sha256HexForPublicKey(publicKey: string): Promise<string> {
  const binary = atob(publicKey.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((publicKey.length + 3) % 4));
  const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
}

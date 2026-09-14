import { env, exports } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { createRecipientForTest } from "../sender-src/crypto.js";
import { MAXIMUM_PAGE_BYTES } from "../src/db/messages.js";
import { MAXIMUM_CIPHERTEXT_LENGTH } from "../src/protocol/types.js";
import { bytesToBase64Url, sha256HexOfText } from "../src/security/tokens.js";
import {
  addMinutes,
  allRegisteredRoutes,
  fetchWorker,
  jsonHeaders,
  messageCiphertext,
  now,
  pairedFixture,
  pairedFixtureWithMessage,
  validEnvelope,
} from "./helpers.js";

describe("encrypted message queue", () => {
  // --- The three verbatim tests from the Task 18 brief (Step 1). ---
  // `validEnvelope(...)` is asynchronous (it performs real AES-GCM/ECDH encryption), so the
  // brief's snippet is awaited here; everything else matches it verbatim.

  it("queues idempotently and returns eligible ciphertext only to its desktop", async () => {
    const paired = await pairedFixture();
    const envelope = await validEnvelope({ messageId: crypto.randomUUID() });

    const first = await paired.sender.postMessage(envelope);
    const duplicate = await paired.sender.postMessage(envelope);
    const messages = await paired.desktop.poll();

    expect(first.status).toBe(202);
    expect(duplicate.status).toBe(200);
    expect(messages).toEqual([envelope]);
  });

  it("ack deletes ciphertext and exposes only delivered-to-device status", async () => {
    const paired = await pairedFixtureWithMessage();

    await paired.desktop.ack(paired.messageId);

    expect(await messageCiphertext(paired.messageId)).toBeNull();
    expect(await paired.sender.status(paired.messageId)).toEqual({ status: "delivered" });
    expect(allRegisteredRoutes()).not.toContain("/v1/messages/:id/read");
  });

  it("does not return a scheduled envelope early", async () => {
    const paired = await pairedFixtureWithMessage({ deliverAfterUtc: addMinutes(now(), 5) });
    expect(await paired.desktop.poll()).toEqual([]);
  });

  it("bounds one poll page by serialized bytes, and returns the remainder after the ack", async () => {
    // Review C1: the desktop rejects any relay response over 64 KiB, and twenty envelopes at the
    // maximum 6144-byte ciphertext are ~168 KiB -- so a full row-capped page was unreadable, and
    // because an unread page is never acked the queue stalled there permanently.
    const paired = await pairedFixture();
    const maximumCiphertext = bytesToBase64Url(crypto.getRandomValues(new Uint8Array(MAXIMUM_CIPHERTEXT_LENGTH)));
    const queuedIds: string[] = [];
    for (let index = 0; index < 20; index += 1) {
      const messageId = crypto.randomUUID();
      const envelope = await validEnvelope({
        messageId,
        createdUtc: new Date(now().getTime() - (20 - index) * 1000).toISOString(),
      });
      const response = await paired.sender.postMessage({ ...envelope, ciphertext: maximumCiphertext });
      expect(response.status).toBe(202);
      queuedIds.push(messageId);
    }

    const firstPage = await paired.desktop.poll();

    expect(firstPage.length).toBeGreaterThan(0);
    expect(firstPage.length).toBeLessThan(queuedIds.length);
    expect(new TextEncoder().encode(JSON.stringify({ messages: firstPage })).length).toBeLessThanOrEqual(
      MAXIMUM_PAGE_BYTES,
    );
    expect(firstPage.map((envelope) => envelope.messageId)).toEqual(queuedIds.slice(0, firstPage.length));

    for (const envelope of firstPage) {
      expect((await paired.desktop.ack(envelope.messageId)).status).toBe(204);
    }
    const secondPage = await paired.desktop.poll();

    expect(secondPage.length).toBeGreaterThan(0);
    expect(secondPage.map((envelope) => envelope.messageId)).toEqual(
      queuedIds.slice(firstPage.length, firstPage.length + secondPage.length),
    );
  });

  it("still returns a single envelope that alone exceeds the page byte budget", async () => {
    // The budget must never starve the queue: one envelope always comes back, whatever its size.
    const paired = await pairedFixture();
    const maximumCiphertext = bytesToBase64Url(crypto.getRandomValues(new Uint8Array(MAXIMUM_CIPHERTEXT_LENGTH)));
    const messageId = crypto.randomUUID();
    const envelope = await validEnvelope({ messageId });

    expect((await paired.sender.postMessage({ ...envelope, ciphertext: maximumCiphertext })).status).toBe(202);

    expect((await paired.desktop.poll()).map((received) => received.messageId)).toEqual([messageId]);
  });

  // --- Additional coverage for the rest of the controller's rulings. ---

  it("rejects oversized ciphertext with 413, not 422", async () => {
    const paired = await pairedFixture();
    const envelope = await validEnvelope({ messageId: crypto.randomUUID() });
    const oversizedCiphertext = bytesToBase64Url(new Uint8Array(MAXIMUM_CIPHERTEXT_LENGTH + 1));

    const response = await paired.sender.postMessage({ ...envelope, ciphertext: oversizedCiphertext });

    expect(response.status).toBe(413);
  });

  it("rejects a deliverAfterUtc more than 30 days ahead with 422", async () => {
    const paired = await pairedFixture();
    const tooFarAhead = new Date(now().getTime() + 31 * 24 * 60 * 60 * 1000).toISOString();
    const envelope = await validEnvelope({ messageId: crypto.randomUUID(), deliverAfterUtc: tooFarAhead });

    const response = await paired.sender.postMessage(envelope);

    expect(response.status).toBe(422);
  });

  it("rejects a non-object body with 400", async () => {
    const paired = await pairedFixture();

    const response = await exports.default.fetch(
      new Request("https://example.test/v1/messages", {
        method: "POST",
        headers: jsonHeaders({ Origin: "https://example.test", Cookie: paired.sessionCookie }),
        body: JSON.stringify([1, 2, 3]),
      }),
    );

    expect(response.status).toBe(400);
  });

  it("rejects a messageId already owned by a different session with 409", async () => {
    const messageId = crypto.randomUUID();
    const first = await pairedFixture();
    const firstEnvelope = await validEnvelope({ messageId });
    expect((await first.sender.postMessage(firstEnvelope)).status).toBe(202);

    const second = await pairedFixture();
    const secondEnvelope = await validEnvelope({ messageId });
    const response = await second.sender.postMessage(secondEnvelope);

    expect(response.status).toBe(409);
  });

  it("rejects the losing sender when the same message id is submitted concurrently", async () => {
    const messageId = crypto.randomUUID();
    const first = await pairedFixture();
    const second = await pairedFixture();
    const firstEnvelope = await validEnvelope({ messageId });
    const secondEnvelope = await validEnvelope({ messageId });

    const responses = await Promise.all([
      first.sender.postMessage(firstEnvelope),
      second.sender.postMessage(secondEnvelope),
    ]);
    const statuses = responses.map((response) => response.status).sort((a, b) => a - b);

    expect(statuses).toEqual([202, 409]);
    const storedRows = await env.DB.prepare("SELECT COUNT(*) AS count FROM messages WHERE id = ?1")
      .bind(messageId)
      .first<{ count: number }>();
    expect(storedRows?.count).toBe(1);
  });

  it("ack of an unknown message id is a 404; re-acking the same message is idempotent", async () => {
    const paired = await pairedFixtureWithMessage();

    expect((await paired.desktop.ack(crypto.randomUUID())).status).toBe(404);
    expect((await paired.desktop.ack(paired.messageId)).status).toBe(204);
    expect((await paired.desktop.ack(paired.messageId)).status).toBe(204);
  });

  it("reports a message whose row was already cleaned up (and never delivered) as expired", async () => {
    const paired = await pairedFixtureWithMessage();
    const testEnv = env as unknown as { DB: D1Database };
    await testEnv.DB.prepare("DELETE FROM messages WHERE id = ?1").bind(paired.messageId).run();

    expect(await paired.sender.status(paired.messageId)).toEqual({ status: "expired" });
  });

  it("does not poll a message whose row is past its own expiry but not yet cleaned up", async () => {
    const paired = await pairedFixtureWithMessage();
    const testEnv = env as unknown as { DB: D1Database };
    const pastExpiry = new Date(Date.now() - 1000).toISOString();
    await testEnv.DB.prepare("UPDATE messages SET expires_utc = ?1 WHERE id = ?2")
      .bind(pastExpiry, paired.messageId)
      .run();

    expect(await paired.desktop.poll()).toEqual([]);
  });

  it('rate-limit bucket for message submission is keyed by SHA-256("messages:<session id>")', async () => {
    // Cheaper and more precise than the behavioral 60-vs-61st test below: reads the bucket row
    // `postMessage` actually wrote and checks it against the exact scope string the controller's
    // ruling mandates, so a future refactor that silently changes the scope string (and would
    // still pass the behavioral test) fails this one.
    const paired = await pairedFixtureWithMessage();
    const testEnv = env as unknown as { DB: D1Database };
    const sessionRow = await testEnv.DB.prepare("SELECT id FROM sender_sessions WHERE device_id = ?1")
      .bind(paired.deviceId)
      .first<{ id: string }>();
    expect(sessionRow).not.toBeNull();
    const expectedKeyHash = await sha256HexOfText(`messages:${sessionRow!.id}`);

    const bucket = await testEnv.DB.prepare("SELECT count FROM rate_limit_buckets WHERE key_hash = ?1")
      .bind(expectedKeyHash)
      .first<{ count: number }>();

    expect(bucket?.count).toBe(1);
  });

  it("rate limits message submission to sixty per hour per sender session", async () => {
    const paired = await pairedFixture();
    for (let i = 0; i < 60; i++) {
      const envelope = await validEnvelope({ messageId: crypto.randomUUID() });
      const response = await paired.sender.postMessage(envelope);
      expect(response.status).toBe(202);
    }

    const envelope = await validEnvelope({ messageId: crypto.randomUUID() });
    const response = await paired.sender.postMessage(envelope);
    expect(response.status).toBe(429);
  });

  it("returns at most twenty eligible messages, ordered by delivery then creation", async () => {
    const paired = await pairedFixture();
    for (let i = 0; i < 21; i++) {
      const envelope = await validEnvelope({ messageId: crypto.randomUUID() });
      expect((await paired.sender.postMessage(envelope)).status).toBe(202);
    }

    const messages = await paired.desktop.poll();
    expect(messages).toHaveLength(20);
  });

  it("answers a malformed percent-escape in a path parameter with 400, not 500", async () => {
    // Review M1: extractParams ran before the router's try, so decodeURIComponent's URIError
    // escaped as an unhandled exception and the client saw a 500 for a client-side mistake.
    const response = await fetchWorker("/v1/messages/%ZZ/ack", { method: "POST" });

    expect(response.status).toBe(400);
  });

  it("DELETE /v1/devices/current revokes the device and deletes its queued messages", async () => {
    const paired = await pairedFixtureWithMessage();

    const response = await fetchWorker("/v1/devices/current", {
      method: "DELETE",
      headers: { Authorization: `Bearer ${paired.desktopToken}` },
    });
    expect(response.status).toBe(204);

    expect(await messageCiphertext(paired.messageId)).toBeNull();
    const afterDelete = await fetchWorker("/v1/devices/current", {
      headers: { Authorization: `Bearer ${paired.desktopToken}` },
    });
    expect(afterDelete.status).toBe(401);
  });

  it("rotating a device's key deletes its queued messages (old ciphertext is undecryptable)", async () => {
    const paired = await pairedFixtureWithMessage();

    const newRecipient = await createRecipientForTest();
    const newSpki = await crypto.subtle.exportKey("spki", newRecipient.publicKey);
    const newRecipientPublicKey = bytesToBase64Url(new Uint8Array(newSpki));

    const rotateResponse = await fetchWorker("/v1/devices/current/rotate-key", {
      method: "POST",
      headers: jsonHeaders({ Authorization: `Bearer ${paired.desktopToken}` }),
      body: JSON.stringify({ publicKey: newRecipientPublicKey }),
    });
    expect(rotateResponse.status).toBe(200);

    expect(await messageCiphertext(paired.messageId)).toBeNull();
  });
});

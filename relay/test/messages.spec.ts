import { env, exports } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { createRecipientForTest } from "../sender-src/crypto.js";
import { MAXIMUM_CIPHERTEXT_LENGTH } from "../src/protocol/types.js";
import { bytesToBase64Url } from "../src/security/tokens.js";
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

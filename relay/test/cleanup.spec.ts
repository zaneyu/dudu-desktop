import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { cleanupExpired } from "../src/cleanup.js";
import type { Env } from "../src/env.js";
import { fetchWorker, messageCiphertext, pairedFixtureWithMessage } from "./helpers.js";

const testEnv = env as unknown as Env;

describe("hourly cleanup", () => {
  it("deletes a 30-day-old undelivered message and its status row", async () => {
    const paired = await pairedFixtureWithMessage();
    const longAgo = new Date(Date.now() - 31 * 24 * 60 * 60 * 1000).toISOString();
    await testEnv.DB.prepare("UPDATE messages SET expires_utc = ?1 WHERE id = ?2")
      .bind(longAgo, paired.messageId)
      .run();
    await testEnv.DB.prepare("UPDATE message_status SET expires_utc = ?1 WHERE id = ?2")
      .bind(longAgo, paired.messageId)
      .run();

    await cleanupExpired(testEnv, new Date());

    expect(await messageCiphertext(paired.messageId)).toBeNull();
    const statusRow = await testEnv.DB.prepare("SELECT 1 FROM message_status WHERE id = ?1")
      .bind(paired.messageId)
      .first();
    expect(statusRow).toBeNull();
  });

  it("keeps a delivered status row until 24 hours after delivery, then deletes it", async () => {
    const paired = await pairedFixtureWithMessage();
    await paired.desktop.ack(paired.messageId);

    await cleanupExpired(testEnv, new Date());
    let statusRow = await testEnv.DB.prepare("SELECT 1 FROM message_status WHERE id = ?1")
      .bind(paired.messageId)
      .first();
    expect(statusRow).not.toBeNull();

    await cleanupExpired(testEnv, new Date(Date.now() + 25 * 60 * 60 * 1000));
    statusRow = await testEnv.DB.prepare("SELECT 1 FROM message_status WHERE id = ?1")
      .bind(paired.messageId)
      .first();
    expect(statusRow).toBeNull();
  });

  it("expires a queued (never-delivered) status row 24h after creation, well before the 30-day ciphertext retention", async () => {
    const paired = await pairedFixtureWithMessage();

    // 25h out: past the status row's 24h-from-creation expiry, but nowhere near the ciphertext's
    // 30-day retention window. If `message_status.expires_utc` had been set to the 30-day value
    // instead of createdUtc+24h, this sweep would not delete it and the status check below would
    // still return 200, not 404.
    await cleanupExpired(testEnv, new Date(Date.now() + 25 * 60 * 60 * 1000));

    expect(await messageCiphertext(paired.messageId)).not.toBeNull();
    const statusResponse = await fetchWorker(`/v1/messages/${paired.messageId}/status`, {
      headers: { Cookie: paired.sessionCookie },
    });
    expect(statusResponse.status).toBe(404);
  });

  it("deletes sender sessions revoked more than 24 hours ago", async () => {
    const paired = await pairedFixtureWithMessage();
    const revokedLongAgo = new Date(Date.now() - 25 * 60 * 60 * 1000).toISOString();
    await testEnv.DB.prepare(
      "UPDATE sender_sessions SET revoked_utc = ?1 WHERE token_hash IN (SELECT token_hash FROM sender_sessions WHERE device_id = ?2)",
    )
      .bind(revokedLongAgo, paired.deviceId)
      .run();

    await cleanupExpired(testEnv, new Date());

    const remaining = await testEnv.DB.prepare("SELECT 1 FROM sender_sessions WHERE device_id = ?1")
      .bind(paired.deviceId)
      .first();
    expect(remaining).toBeNull();
  });

  it("deletes rate-limit buckets whose hourly window has long since ended", async () => {
    const staleWindow = new Date(Date.now() - 2 * 60 * 60 * 1000).toISOString();
    await testEnv.DB.prepare(
      "INSERT INTO rate_limit_buckets (key_hash, window_start_utc, count) VALUES (?1, ?2, 1)",
    )
      .bind("stale-bucket-for-cleanup-test", staleWindow)
      .run();

    await cleanupExpired(testEnv, new Date());

    const remaining = await testEnv.DB.prepare("SELECT 1 FROM rate_limit_buckets WHERE key_hash = ?1")
      .bind("stale-bucket-for-cleanup-test")
      .first();
    expect(remaining).toBeNull();
  });
});

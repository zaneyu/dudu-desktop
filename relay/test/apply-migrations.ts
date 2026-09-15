/**
 * Workers-project setup file: applies the migrations passed in as the `TEST_MIGRATIONS` binding
 * (see vitest.config.ts, which reads them via `readD1Migrations()` on the Node side) to the local
 * D1 database before any test in this project runs.
 */
import { applyD1Migrations } from "cloudflare:test";
import { beforeEach } from "vitest";
import { env } from "cloudflare:workers";
import type { D1Migration } from "@cloudflare/vitest-plugin";

const { DB, TEST_MIGRATIONS } = env as unknown as {
  DB: D1Database;
  TEST_MIGRATIONS: D1Migration[];
};

await applyD1Migrations(DB, TEST_MIGRATIONS);

// Rate-limit buckets are global state that D1 keeps across tests in a file; the IP-independent
// caps (register, redeem failures) would otherwise trip in suites that pair many devices.
beforeEach(async () => {
  await DB.prepare("DELETE FROM rate_limit_buckets").run();
});

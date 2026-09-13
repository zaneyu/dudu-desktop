import path from "node:path";
import { fileURLToPath } from "node:url";
import { cloudflareTest, readD1Migrations } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

const dirname = path.dirname(fileURLToPath(import.meta.url));
const migrationsPath = path.join(dirname, "migrations");
const wranglerConfigPath = path.join(dirname, "wrangler.jsonc");

// A non-secret pepper is fine here: it only needs to be stable within a single test run so
// pairing-code HMACs are reproducible across requests in the same test.
const TEST_PAIRING_CODE_PEPPER = "test-only-pairing-code-pepper-do-not-use-in-prod";

export default defineConfig({
  test: {
    projects: [
      {
        test: {
          name: "node",
          environment: "node",
          include: ["test/crypto.spec.ts"],
        },
      },
      {
        plugins: [
          cloudflareTest(async () => {
            const migrations = await readD1Migrations(migrationsPath);
            return {
              wrangler: { configPath: wranglerConfigPath },
              miniflare: {
                bindings: {
                  TEST_MIGRATIONS: migrations,
                  PAIRING_CODE_PEPPER: TEST_PAIRING_CODE_PEPPER,
                },
              },
            };
          }),
        ],
        test: {
          name: "workers",
          include: ["test/**/*.spec.ts"],
          exclude: ["test/crypto.spec.ts"],
          setupFiles: ["./test/apply-migrations.ts"],
        },
      },
    ],
  },
});

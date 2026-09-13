/**
 * Single source of truth for the Worker's bindings and vars.
 *
 * `worker-configuration.d.ts` (regenerate with `npm run types`) declares the ambient global
 * `Cloudflare.Env` for every binding `wrangler.jsonc` knows about (`DB`, `ASSETS`).
 * `PAIRING_CODE_PEPPER` is a Worker secret (`wrangler secret put PAIRING_CODE_PEPPER` in
 * production; a test binding in `vitest.config.ts`) that `wrangler.jsonc` intentionally does not
 * declare, so it is added here by hand. Every route imports `Env` from this module rather than
 * referencing the ambient ones directly.
 */
export interface Env extends Cloudflare.Env {
  PAIRING_CODE_PEPPER: string;
}

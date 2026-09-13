import { deleteStaleSenderSessions } from "./db/devices.js";
import { deleteExpiredPairingCodes } from "./db/pairings.js";
import type { Env } from "./env.js";
import { Router } from "./http/router.js";
import { createDevicePairingCode, getCurrentDevice, registerDevice, rotateDeviceKey } from "./routes/devices.js";
import { disconnectSender, getSenderDevice, redeemPairing } from "./routes/pairings.js";

const CLEANUP_GRACE_MS = 24 * 60 * 60 * 1000;

const router = new Router();
router.add("POST", "/v1/devices/register", registerDevice);
router.add("GET", "/v1/devices/current", getCurrentDevice);
router.add("POST", "/v1/devices/current/rotate-key", rotateDeviceKey);
router.add("POST", "/v1/devices/pairing-code", createDevicePairingCode);
router.add("POST", "/v1/pairings/redeem", redeemPairing);
router.add("GET", "/v1/sender/device", getSenderDevice);
router.add("POST", "/v1/sender/disconnect", disconnectSender);

export { router };

export default {
  async fetch(request, env, ctx) {
    return router.handle(request, env, ctx);
  },

  /** Hourly: deletes expired pairing codes, and sender sessions revoked/expired >24h ago. */
  async scheduled(_event, env) {
    const nowIso = new Date().toISOString();
    const cutoffIso = new Date(Date.now() - CLEANUP_GRACE_MS).toISOString();
    await deleteExpiredPairingCodes(env.DB, nowIso);
    await deleteStaleSenderSessions(env.DB, cutoffIso);
  },
} satisfies ExportedHandler<Env>;

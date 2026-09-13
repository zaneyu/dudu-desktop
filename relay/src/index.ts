/**
 * Task 21 note: the private-note and outage hardening (security response headers, the
 * size-bounded/prototype-safe JSON body reader, and noncanonical-Base64URL envelope rejection)
 * lives in `http/responses.ts`, `http/body.ts`, and `security/envelopeValidation.ts`
 * respectively, and applies to every route below automatically because each already routes
 * through those modules — this file stays the wiring point only, with no route-by-route change
 * needed.
 */
import { cleanupExpired } from "./cleanup.js";
import type { Env } from "./env.js";
import { Router } from "./http/router.js";
import {
  createDevicePairingCode,
  deleteCurrentDevice,
  getCurrentDevice,
  registerDevice,
  rotateDeviceKey,
} from "./routes/devices.js";
import { ackMessage, getMessages, getMessageStatus, postMessage } from "./routes/messages.js";
import { disconnectSender, getSenderDevice, redeemPairing } from "./routes/pairings.js";

const router = new Router();
router.add("POST", "/v1/devices/register", registerDevice);
router.add("GET", "/v1/devices/current", getCurrentDevice);
router.add("DELETE", "/v1/devices/current", deleteCurrentDevice);
router.add("POST", "/v1/devices/current/rotate-key", rotateDeviceKey);
router.add("POST", "/v1/devices/pairing-code", createDevicePairingCode);
router.add("POST", "/v1/pairings/redeem", redeemPairing);
router.add("GET", "/v1/sender/device", getSenderDevice);
router.add("POST", "/v1/sender/disconnect", disconnectSender);
router.add("POST", "/v1/messages", postMessage);
router.add("GET", "/v1/messages", getMessages);
router.add("POST", "/v1/messages/:id/ack", ackMessage);
router.add("GET", "/v1/messages/:id/status", getMessageStatus);

export { router };

export default {
  async fetch(request, env, ctx) {
    return router.handle(request, env, ctx);
  },

  /** Hourly: see `cleanup.ts` for the full list of what this sweeps. */
  async scheduled(_event, env) {
    await cleanupExpired(env, new Date());
  },
} satisfies ExportedHandler<Env>;

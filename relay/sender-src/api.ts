/**
 * Thin `fetch` wrapper for the relay's sender-facing HTTP routes (Tasks 17-18). Every mutating
 * call uses `credentials: "same-origin"` so the browser attaches the `__Host-dudu_sender`
 * cookie and sends the `Origin` header the relay requires; nothing here ever reads or logs that
 * cookie's value.
 */
import type {
  EncryptedEnvelopeV1,
  GetMessageStatusResponse,
  GetSenderDeviceResponse,
  PostMessageResponse,
  RedeemPairingResponse,
  RelayErrorResponse,
} from "../src/protocol/types.js";

const SEND_TIMEOUT_MS = 15_000;
/**
 * Every other relay call gets the same bound: a phone on a flaky connection can leave a bare
 * `fetch` pending for minutes, which would leave the pairing button stuck on "pairing" or the
 * page stuck checking a cached pairing on load with no way to retry.
 */
const REQUEST_TIMEOUT_MS = 15_000;

/** `fetch` with an abort timeout; any rejection (offline, DNS, timeout) becomes ApiNetworkError. */
async function fetchWithTimeout(url: string, init: RequestInit, timeoutMs = REQUEST_TIMEOUT_MS): Promise<Response> {
  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetch(url, { ...init, signal: controller.signal });
  } catch {
    throw new ApiNetworkError("Could not reach the relay.");
  } finally {
    clearTimeout(timeoutId);
  }
}

/** The relay says the sender session is gone (401). Callers should drop to the unpaired state. */
export class ApiUnauthorizedError extends Error {}

/** `fetch` itself failed: offline, DNS, CORS, or the 15s send timeout aborted it. */
export class ApiNetworkError extends Error {}

/** Any other non-success HTTP status the relay returned. */
export class ApiHttpError extends Error {
  constructor(
    public readonly status: number,
    public readonly body: RelayErrorResponse | null,
  ) {
    super(body?.message ?? `Request failed with status ${status}.`);
  }
}

async function parseErrorBody(response: Response): Promise<RelayErrorResponse | null> {
  try {
    return (await response.json()) as RelayErrorResponse;
  } catch {
    return null;
  }
}

export async function redeemPairing(code: string): Promise<RedeemPairingResponse> {
  const response = await fetchWithTimeout("/v1/pairings/redeem", {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ code }),
  });
  if (response.status === 200) {
    return (await response.json()) as RedeemPairingResponse;
  }
  throw new ApiHttpError(response.status, await parseErrorBody(response));
}

/** Returns null when the relay reports 401 (no live session). Throws for any other failure. */
export async function getSenderDevice(): Promise<GetSenderDeviceResponse | null> {
  const response = await fetchWithTimeout("/v1/sender/device", { method: "GET", credentials: "same-origin" });
  if (response.status === 401) {
    return null;
  }
  if (response.status === 200) {
    return (await response.json()) as GetSenderDeviceResponse;
  }
  throw new ApiHttpError(response.status, await parseErrorBody(response));
}

/** Clears local pairing state only after the relay confirms revocation with HTTP 204. */
export async function disconnectSender(): Promise<void> {
  const response = await fetchWithTimeout("/v1/sender/disconnect", { method: "POST", credentials: "same-origin" });
  if (response.status === 204) {
    return;
  }
  throw new ApiHttpError(response.status, await parseErrorBody(response));
}

export async function postMessage(
  envelope: EncryptedEnvelopeV1,
  recipient: { deviceId: string; publicKey: string },
): Promise<PostMessageResponse> {
  const response = await fetchWithTimeout(
    "/v1/messages",
    {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "X-Dudu-Device": recipient.deviceId,
        "X-Dudu-Recipient-Key": recipient.publicKey,
      },
      body: JSON.stringify(envelope),
    },
    SEND_TIMEOUT_MS,
  );
  if (response.status === 401) {
    throw new ApiUnauthorizedError("The sender session is gone.");
  }
  if (response.status === 202 || response.status === 200) {
    return (await response.json()) as PostMessageResponse;
  }
  throw new ApiHttpError(response.status, await parseErrorBody(response));
}

export async function getMessageStatus(messageId: string): Promise<GetMessageStatusResponse | null> {
  const response = await fetchWithTimeout(`/v1/messages/${encodeURIComponent(messageId)}/status`, {
    method: "GET",
    credentials: "same-origin",
  });
  if (response.status === 401) {
    throw new ApiUnauthorizedError("The sender session is gone.");
  }
  if (response.status === 404) {
    return null;
  }
  if (response.status === 200) {
    return (await response.json()) as GetMessageStatusResponse;
  }
  throw new ApiHttpError(response.status, await parseErrorBody(response));
}

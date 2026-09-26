/**
 * Wires `public/index.html`'s two states together: the unpaired pairing form and the paired
 * composer. No framework, no client router — just DOM lookups and event listeners.
 */
import { ApiHttpError, ApiNetworkError } from "./api.js";
import { MessageComposer, type ComposerElements, type ComposerSessionLoss } from "./composer.js";
import {
  clearStoredDevice,
  disconnect,
  isWellFormedPairingCode,
  loadStoredDevice,
  normalizePairingCodeTyping,
  pairWithCode,
  PairingAuthenticityError,
  verifyStoredSession,
  type StoredDevice,
} from "./pairing.js";
import { clearRecent, StatusTracker } from "./status.js";

const SESSION_LOST_MESSAGE = "phone disconnected pair again";
// Review I6: the pinned key no longer matches what the relay reports. Deliberately blunt --
// this is the one case where pairing again is not just housekeeping.
const KEY_CHANGED_MESSAGE = "dudu key changed pair again pls";
const DISCONNECTED_MESSAGE = "disconnected le get a new code to pair again";
const MALFORMED_CODE_MESSAGE = "the code is 8 letters and numbers check it again";
// Shown when a cached pairing exists but the relay could not be reached to confirm it on load:
// the phone is probably still paired, so it must not look like a fresh, never-paired page.
const OFFLINE_ON_LOAD_MESSAGE = "couldnt reach dudu check your connection ill retry when youre back online";
const CONFIRM_DISCONNECT_MESSAGE =
  "Disconnect this phone? You will need a new pairing code from the desktop to send notes again.";

function requireElement<T extends HTMLElement>(id: string): T {
  const element = document.getElementById(id);
  if (!element) {
    throw new Error(`Missing required element #${id}`);
  }
  return element as T;
}

const unpairedSection = requireElement<HTMLElement>("unpaired-section");
const pairedSection = requireElement<HTMLElement>("paired-section");
const pairingForm = requireElement<HTMLFormElement>("pairing-form");
const pairingCodeInput = requireElement<HTMLInputElement>("pairing-code");
const pairingStatus = requireElement<HTMLElement>("pairing-status");
const pairButton = requireElement<HTMLButtonElement>("pair-button");
const messageTextArea = requireElement<HTMLTextAreaElement>("message-text");
const disconnectStatus = requireElement<HTMLElement>("disconnect-status");
const disconnectButton = requireElement<HTMLButtonElement>("disconnect-button");
const recentList = requireElement<HTMLElement>("recent-statuses");

const composerElements: ComposerElements = {
  form: requireElement("composer-form"),
  textArea: requireElement("message-text"),
  counter: requireElement("char-counter"),
  reactionInputs: document.querySelectorAll<HTMLInputElement>('input[name="reaction"]'),
  sendLaterInput: requireElement("send-later"),
  scheduleStatus: requireElement("schedule-status"),
  previewButton: requireElement("preview-button"),
  previewDialog: requireElement<HTMLDialogElement>("preview-dialog"),
  previewText: requireElement("preview-text"),
  previewReaction: requireElement("preview-reaction"),
  previewClose: requireElement("preview-close"),
  sendButton: requireElement("send-button"),
  sendStatus: requireElement("send-status"),
};

interface ShowOptions {
  /** Move keyboard focus into the newly shown state. Off for the initial page load, where
   * focusing a field would pop the phone keyboard before the partner asked for it. */
  focus?: boolean;
}

/** A failed attempt with the code in the field: flagged on the field, cleared once it is edited. */
function setPairingError(message: string): void {
  pairingStatus.textContent = message;
  if (message) {
    pairingCodeInput.setAttribute("aria-invalid", "true");
  } else {
    pairingCodeInput.removeAttribute("aria-invalid");
  }
}

/** An explanation of why the page is unpaired (key changed, session lost). Not about the field's
 * contents, so it is not cleared by typing a new code. */
function setPairingNotice(message: string): void {
  pairingStatus.textContent = message;
  pairingCodeInput.removeAttribute("aria-invalid");
}

function showUnpaired(message = "", options: ShowOptions = {}): void {
  statusTracker.stop();
  composer.clearRecipient();
  pairedSection.hidden = true;
  unpairedSection.hidden = false;
  // Nothing from the paired state (e.g. a stale "disconnecting") may survive into the next one.
  disconnectStatus.textContent = "";
  setPairingNotice(message);
  if (options.focus) {
    pairingCodeInput.focus();
  }
}

function showPaired(device: StoredDevice, options: ShowOptions = {}): void {
  composer.setRecipientPublicKey(device.publicKey, device.deviceId);
  unpairedSection.hidden = true;
  pairedSection.hidden = false;
  disconnectStatus.textContent = "";
  setPairingNotice("");
  statusTracker.render();
  statusTracker.start();
  if (options.focus) {
    // The pairing button that had focus just disappeared; land the partner in the composer.
    messageTextArea.focus();
  }
}

function handleSessionLoss(reason: ComposerSessionLoss): void {
  if (reason === "recipient-changed") {
    // Same treatment as a key change found on load: drop the pinned key, say so bluntly.
    clearStoredDevice();
    showUnpaired(KEY_CHANGED_MESSAGE, { focus: true });
    return;
  }
  clearStoredDevice();
  showUnpaired(SESSION_LOST_MESSAGE, { focus: true });
}

const composer = new MessageComposer(composerElements, {
  onUnauthorized: handleSessionLoss,
  onSent: () => statusTracker.render(),
});
const statusTracker = new StatusTracker(recentList, () => handleSessionLoss("session-lost"));

let pairing = false;

function setPairingBusy(busy: boolean): void {
  pairing = busy;
  pairButton.disabled = busy;
  pairingForm.setAttribute("aria-busy", String(busy));
}

async function handlePair(rawCode: string): Promise<void> {
  // A second submit while the first redemption is in flight would spend another of the relay's
  // rate-limited attempts on a code the first request is already consuming.
  if (pairing) {
    return;
  }
  const code = normalizePairingCodeTyping(rawCode);
  if (!isWellFormedPairingCode(code)) {
    // Caught locally: a malformed code can never redeem, so it must not cost a relay attempt.
    setPairingError(MALFORMED_CODE_MESSAGE);
    pairingCodeInput.focus();
    return;
  }
  setPairingBusy(true);
  pairingStatus.textContent = "pairing";
  pairingCodeInput.removeAttribute("aria-invalid");
  try {
    const device = await pairWithCode(code);
    pairingCodeInput.value = "";
    showPaired(device, { focus: true });
  } catch (error) {
    if (error instanceof ApiHttpError) {
      setPairingError(
        // The relay answers 410 for unknown, mistyped, used, AND expired codes alike, so the
        // copy must not claim the code expired when it may just have a typo.
        error.status === 410
          ? "aiyo that code didnt work or expired check it or get a new one"
          : error.status === 429
            ? "too many tries wait about an hour then try again"
            : "aiyo that code didnt work",
      );
    } else if (error instanceof PairingAuthenticityError) {
      setPairingError("aiyo device fingerprint didnt match pair again");
    } else if (error instanceof ApiNetworkError) {
      setPairingError("aiyo couldnt reach it try again");
    } else {
      setPairingError("aiyo something broke try again");
    }
  } finally {
    setPairingBusy(false);
  }
}

async function handleDisconnect(): Promise<void> {
  // Revoking the session cannot be undone from the phone -- pairing again needs a fresh code
  // from the desktop -- so a stray tap must not do it.
  if (!window.confirm(CONFIRM_DISCONNECT_MESSAGE)) {
    return;
  }
  disconnectStatus.textContent = "disconnecting";
  disconnectButton.disabled = true;
  try {
    await disconnect();
    clearRecent();
    showUnpaired(DISCONNECTED_MESSAGE, { focus: true });
  } catch (error) {
    disconnectStatus.textContent =
      error instanceof ApiNetworkError || error instanceof ApiHttpError
        ? "aiyo couldnt disconnect try again"
        : "aiyo something broke try again";
  } finally {
    disconnectButton.disabled = false;
  }
}

async function bootstrap(): Promise<void> {
  try {
    const verified = await verifyStoredSession();
    if (verified.state === "paired") {
      showPaired(verified.device);
      return;
    }
    if (verified.state === "key-changed") {
      showUnpaired(KEY_CHANGED_MESSAGE);
      return;
    }
  } catch {
    // verifyStoredSession only throws for a relay failure other than "no session", so the cached
    // device is left in place. Say why the pairing form is showing and retry once the phone is
    // back online, rather than silently presenting a paired phone as never paired.
    if (loadStoredDevice()) {
      showUnpaired(OFFLINE_ON_LOAD_MESSAGE);
      window.addEventListener("online", () => void bootstrap(), { once: true });
      return;
    }
  }
  showUnpaired();
}

pairingForm.addEventListener("submit", (event) => {
  event.preventDefault();
  void handlePair(pairingCodeInput.value);
});

pairingCodeInput.addEventListener("input", () => {
  // Clean up spaces, dashes, lowercase, and O/I/L look-alikes as they are typed or pasted,
  // keeping the caret where it was relative to the characters that survive.
  const raw = pairingCodeInput.value;
  const normalized = normalizePairingCodeTyping(raw);
  if (normalized !== raw) {
    const caret = pairingCodeInput.selectionStart ?? raw.length;
    const caretAfter = Math.min(normalizePairingCodeTyping(raw.slice(0, caret)).length, normalized.length);
    pairingCodeInput.value = normalized;
    pairingCodeInput.setSelectionRange(caretAfter, caretAfter);
  }
  if (pairingCodeInput.getAttribute("aria-invalid") === "true" && !pairing) {
    setPairingError("");
  }
});

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") {
    void statusTracker.refresh();
  }
});

disconnectButton.addEventListener("click", () => {
  void handleDisconnect();
});

void bootstrap();

window.addEventListener("pagehide", () => {
  pairingCodeInput.value = "";
  composer.clearSensitiveDraft();
});

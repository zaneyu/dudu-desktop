/**
 * Wires `public/index.html`'s two states together: the unpaired pairing form and the paired
 * composer. No framework, no client router — just DOM lookups and event listeners.
 */
import { ApiHttpError, ApiNetworkError } from "./api.js";
import { MessageComposer, type ComposerElements } from "./composer.js";
import {
  disconnect,
  isWellFormedPairingCode,
  normalizePairingCode,
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
const RELAY_UNREACHABLE_MESSAGE = "couldnt reach dudu trying again";
const BOOT_RETRY_DELAYS_MS = [5_000, 15_000, 30_000, 60_000];

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
const pairingButton = requireElement<HTMLButtonElement>("pairing-button");
const bootStatus = requireElement<HTMLElement>("boot-status");
const pairedGreeting = requireElement<HTMLElement>("paired-greeting");
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

let bootRetryTimer: ReturnType<typeof setTimeout> | null = null;
let bootAttempt = 0;
let pairing = false;

function cancelBootRetry(): void {
  if (bootRetryTimer !== null) {
    clearTimeout(bootRetryTimer);
    bootRetryTimer = null;
  }
}

/** Moves focus into the section just shown when the section that held it was just hidden, so
 * keyboard and screen-reader users are not dropped onto <body>. */
function moveFocusIfLost(hiddenSection: HTMLElement, target: HTMLElement): void {
  const active = document.activeElement;
  if (active === null || active === document.body || hiddenSection.contains(active)) {
    target.focus();
  }
}

function showUnpaired(message = ""): void {
  cancelBootRetry();
  statusTracker.stop();
  composer.clearRecipient();
  const hadFocus = pairedSection.contains(document.activeElement);
  pairedSection.hidden = true;
  unpairedSection.hidden = false;
  bootStatus.textContent = "";
  disconnectStatus.textContent = "";
  // Set after un-hiding: text written into a live region inside a hidden section is not announced.
  pairingStatus.textContent = message;
  pairingCodeInput.removeAttribute("aria-invalid");
  if (hadFocus) {
    moveFocusIfLost(pairedSection, pairingCodeInput);
  }
}

/** The relay says this phone's session is gone. Status lookups are scoped to the session, so the
 * old "recent" entries can never resolve again; drop them rather than showing a list of
 * "status no longer available" after re-pairing. */
function showSessionLost(): void {
  clearRecent();
  showUnpaired(SESSION_LOST_MESSAGE);
}

function showPaired(device: StoredDevice): void {
  cancelBootRetry();
  composer.setRecipientPublicKey(device.publicKey, device.deviceId);
  const hadFocus = unpairedSection.contains(document.activeElement);
  unpairedSection.hidden = true;
  pairedSection.hidden = false;
  bootStatus.textContent = "";
  disconnectStatus.textContent = "";
  statusTracker.render();
  statusTracker.start();
  if (hadFocus) {
    moveFocusIfLost(unpairedSection, pairedGreeting);
  }
}

const composer = new MessageComposer(composerElements, {
  onUnauthorized: showSessionLost,
  onSent: () => statusTracker.refresh(),
});
const statusTracker = new StatusTracker(recentList, showSessionLost);

function setPairingBusy(busy: boolean): void {
  pairing = busy;
  pairingButton.disabled = busy;
  pairingCodeInput.readOnly = busy;
}

async function handlePair(rawCode: string): Promise<void> {
  if (pairing) return;
  const code = normalizePairingCode(rawCode);
  if (!isWellFormedPairingCode(code)) {
    // Checked here, before the relay: a malformed try would otherwise burn one of the ten
    // hourly redeem attempts and come back as the same vague "didnt work".
    pairingStatus.textContent = code
      ? "check the code its 8 letters and numbers"
      : "type the code from the desktop app first";
    pairingCodeInput.setAttribute("aria-invalid", "true");
    pairingCodeInput.focus();
    return;
  }
  pairingCodeInput.value = code;
  pairingCodeInput.removeAttribute("aria-invalid");
  setPairingBusy(true);
  pairingStatus.textContent = "pairing";
  try {
    const device = await pairWithCode(code);
    pairingCodeInput.value = "";
    showPaired(device);
  } catch (error) {
    if (error instanceof ApiHttpError) {
      pairingStatus.textContent =
        error.status === 410
          ? "code expired try a new one"
          : error.status === 429
            ? "too many tries wait about an hour then try again"
            : error.status === 400
              ? "check the code its 8 letters and numbers"
              : "aiyo that code didnt work";
    } else if (error instanceof PairingAuthenticityError) {
      pairingStatus.textContent = "aiyo device fingerprint didnt match pair again";
    } else if (error instanceof ApiNetworkError) {
      pairingStatus.textContent = "aiyo couldnt reach it try again";
    } else {
      pairingStatus.textContent = "aiyo something broke try again";
    }
  } finally {
    setPairingBusy(false);
  }
}

async function handleDisconnect(): Promise<void> {
  disconnectStatus.textContent = "disconnecting";
  disconnectButton.disabled = true;
  try {
    await disconnect();
    clearRecent();
    disconnectStatus.textContent = "";
    showUnpaired();
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
  cancelBootRetry();
  try {
    const verified = await verifyStoredSession();
    bootAttempt = 0;
    if (verified.state === "paired") {
      showPaired(verified.device);
      return;
    }
    if (verified.state === "key-changed") {
      showUnpaired(KEY_CHANGED_MESSAGE);
      return;
    }
    showUnpaired();
  } catch {
    // verifyStoredSession only throws when the relay could not be reached or failed (offline,
    // 5xx) -- the cached pairing is intact. Showing the pairing form here told a paired person on
    // flaky mobile data that they had to pair again. Say what happened and retry instead.
    unpairedSection.hidden = true;
    pairedSection.hidden = true;
    bootStatus.textContent = RELAY_UNREACHABLE_MESSAGE;
    const delay = BOOT_RETRY_DELAYS_MS[Math.min(bootAttempt, BOOT_RETRY_DELAYS_MS.length - 1)];
    bootAttempt += 1;
    bootRetryTimer = setTimeout(() => void bootstrap(), delay);
  }
}

window.addEventListener("online", () => {
  if (bootRetryTimer !== null) {
    void bootstrap();
  }
});

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") {
    statusTracker.refresh();
  }
});

pairingCodeInput.addEventListener("input", () => {
  pairingCodeInput.removeAttribute("aria-invalid");
});

pairingForm.addEventListener("submit", (event) => {
  event.preventDefault();
  void handlePair(pairingCodeInput.value);
});

disconnectButton.addEventListener("click", () => {
  void handleDisconnect();
});

void bootstrap();

window.addEventListener("pagehide", () => {
  pairingCodeInput.value = "";
  composer.clearSensitiveDraft();
});

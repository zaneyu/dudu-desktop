/**
 * Wires `public/index.html`'s two states together: the unpaired pairing form and the paired
 * composer. No framework, no client router — just DOM lookups and event listeners.
 */
import { ApiHttpError, ApiNetworkError } from "./api.js";
import { MessageComposer, type ComposerElements } from "./composer.js";
import { disconnect, pairWithCode, verifyStoredSession, type StoredDevice } from "./pairing.js";
import { clearRecent, StatusTracker } from "./status.js";

const SESSION_LOST_MESSAGE = "phone disconnected pair again";
// Review I6: the pinned key no longer matches what the relay reports. Deliberately blunt --
// this is the one case where pairing again is not just housekeeping.
const KEY_CHANGED_MESSAGE = "dudu key changed pair again pls";

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

function showUnpaired(message = ""): void {
  statusTracker.stop();
  pairedSection.hidden = true;
  unpairedSection.hidden = false;
  pairingStatus.textContent = message;
}

function showPaired(device: StoredDevice): void {
  composer.setRecipientPublicKey(device.publicKey);
  unpairedSection.hidden = true;
  pairedSection.hidden = false;
  statusTracker.render();
  statusTracker.start();
}

const composer = new MessageComposer(composerElements, {
  onUnauthorized: () => showUnpaired(SESSION_LOST_MESSAGE),
});
const statusTracker = new StatusTracker(recentList, () => showUnpaired(SESSION_LOST_MESSAGE));

async function handlePair(code: string): Promise<void> {
  pairingStatus.textContent = "pairing";
  try {
    const device = await pairWithCode(code);
    pairingCodeInput.value = "";
    showPaired(device);
  } catch (error) {
    if (error instanceof ApiHttpError) {
      pairingStatus.textContent = error.status === 410 ? "code expired try a new one" : "aiyo that code didnt work";
    } else if (error instanceof ApiNetworkError) {
      pairingStatus.textContent = "aiyo couldnt reach it try again";
    } else {
      pairingStatus.textContent = "aiyo something broke try again";
    }
  }
}

async function handleDisconnect(): Promise<void> {
  await disconnect();
  clearRecent();
  showUnpaired();
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
    // Fall through to the unpaired state below; verifyStoredSession only throws for a relay
    // failure other than "no session", so the cached device is left in place for a later retry.
  }
  showUnpaired();
}

pairingForm.addEventListener("submit", (event) => {
  event.preventDefault();
  void handlePair(pairingCodeInput.value);
});

disconnectButton.addEventListener("click", () => {
  void handleDisconnect();
});

void bootstrap();

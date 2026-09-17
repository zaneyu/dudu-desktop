import { ApiHttpError, ApiNetworkError, ApiUnauthorizedError, postMessage } from "./api.js";
import { encryptPayloadBytes, importRecipientPublicKey, zeroPayloadBytes } from "./crypto.js";
import { addRecentStatus } from "./status.js";
import {
  MAXIMUM_PAYLOAD_UTF8_BYTES,
  MAXIMUM_TEXT_SCALAR_VALUES,
  MESSAGE_RETENTION_DAYS,
  type EncryptedEnvelopeV1,
  type Reaction,
  type RemoteMessagePayloadV1,
} from "../src/protocol/types.js";

export interface ComposerElements {
  form: HTMLFormElement;
  textArea: HTMLTextAreaElement;
  counter: HTMLElement;
  reactionInputs: NodeListOf<HTMLInputElement>;
  sendLaterInput: HTMLInputElement;
  scheduleStatus: HTMLElement;
  previewButton: HTMLButtonElement;
  previewDialog: HTMLDialogElement;
  previewText: HTMLElement;
  previewReaction: HTMLElement;
  previewClose: HTMLButtonElement;
  sendButton: HTMLButtonElement;
  sendStatus: HTMLElement;
}

export interface ComposerCallbacks {
  onUnauthorized: () => void;
}

export class MessageComposer {
  private recipient: { publicKey: string; deviceId: string } | null = null;
  private pendingEnvelope: EncryptedEnvelopeV1 | null = null;
  private revision = 0;
  private generation = 0;
  private busy = false;

  constructor(
    private readonly elements: ComposerElements,
    private readonly callbacks: ComposerCallbacks,
  ) {
    this.wire();
    this.updateCounter();
  }

  setRecipientPublicKey(publicKey: string, deviceId: string): void {
    this.clearRecipient();
    this.recipient = { publicKey, deviceId };
  }

  clearRecipient(): void {
    this.recipient = null;
    this.clearSensitiveDraft();
  }

  reset(): void {
    this.elements.textArea.value = "";
    this.elements.sendLaterInput.value = "";
    this.elements.scheduleStatus.textContent = "";
    this.invalidateDraft();
    this.updateCounter();
  }

  clearSensitiveDraft(): void {
    this.generation++;
    this.reset();
    this.elements.sendStatus.textContent = "";
  }

  private clearPreview(): void {
    this.elements.previewText.textContent = "";
    this.elements.previewReaction.textContent = "";
    this.elements.previewDialog.close();
  }

  private invalidateDraft(): void {
    this.revision++;
    this.pendingEnvelope = null;
    this.clearPreview();
  }

  private wire(): void {
    this.elements.textArea.addEventListener("input", () => {
      this.invalidateDraft();
      this.updateCounter();
    });
    for (const input of this.elements.reactionInputs) {
      input.addEventListener("change", () => this.invalidateDraft());
    }
    this.elements.sendLaterInput.addEventListener("input", () => this.invalidateDraft());
    this.elements.previewButton.addEventListener("click", () => {
      this.elements.previewText.textContent = this.elements.textArea.value;
      this.elements.previewReaction.textContent = `reaction ${this.currentReaction()}`;
      this.elements.previewDialog.show();
    });
    this.elements.previewClose.addEventListener("click", () => this.clearPreview());
    this.elements.form.addEventListener("submit", (event) => {
      event.preventDefault();
      void this.send();
    });
  }

  private updateCounter(): void {
    this.elements.counter.textContent = `${Array.from(this.elements.textArea.value).length} / ${MAXIMUM_TEXT_SCALAR_VALUES}`;
  }

  private currentReaction(): Reaction {
    for (const input of this.elements.reactionInputs) {
      if (input.checked) return input.value as Reaction;
    }
    return "none";
  }

  private resolveDeliverAfterUtc(): string | null | undefined {
    const raw = this.elements.sendLaterInput.value;
    if (!raw) {
      this.elements.scheduleStatus.textContent = "";
      return null;
    }
    const scheduledMs = new Date(raw).getTime();
    if (!Number.isFinite(scheduledMs) || scheduledMs <= Date.now()) {
      this.elements.scheduleStatus.textContent = "choose a future time";
      return undefined;
    }
    if (scheduledMs - Date.now() > MESSAGE_RETENTION_DAYS * 86400_000) {
      this.elements.scheduleStatus.textContent = "cant schedule that far ahead";
      return undefined;
    }
    this.elements.scheduleStatus.textContent = "";
    return new Date(scheduledMs).toISOString();
  }

  private async send(): Promise<void> {
    const recipient = this.recipient;
    if (!recipient || this.busy) return;
    const deliverAfterUtc = this.pendingEnvelope?.deliverAfterUtc ?? this.resolveDeliverAfterUtc();
    if (deliverAfterUtc === undefined) return;
    const text = this.elements.textArea.value;
    if (!text.trim()) {
      this.elements.sendStatus.textContent = "write something first";
      return;
    }
    const payload: RemoteMessagePayloadV1 = { kind: "note", text, reaction: this.currentReaction() };
    const payloadBytes = new TextEncoder().encode(JSON.stringify(payload));
    if (payloadBytes.byteLength > MAXIMUM_PAYLOAD_UTF8_BYTES || Array.from(text).length > MAXIMUM_TEXT_SCALAR_VALUES) {
      zeroPayloadBytes(payloadBytes);
      this.elements.sendStatus.textContent = "aiyo too long trim it abit";
      return;
    }
    const revision = this.revision;
    const generation = this.generation;
    this.busy = true;
    this.elements.sendButton.disabled = true;
    this.elements.sendStatus.textContent = "sending";
    try {
      let envelope = this.pendingEnvelope;
      try {
        if (!envelope) {
          const key = await importRecipientPublicKey(recipient.publicKey);
          envelope = await encryptPayloadBytes(key, payloadBytes, { messageId: crypto.randomUUID(), deliverAfterUtc });
        }
      } finally {
        zeroPayloadBytes(payloadBytes);
      }
      if (generation !== this.generation) return;
      if (revision === this.revision) this.pendingEnvelope = envelope;
      const result = await postMessage(envelope, recipient);
      if (generation !== this.generation) return;
      addRecentStatus(result.messageId, result.status);
      if (revision === this.revision) this.reset();
      this.elements.sendStatus.textContent = result.status === "delivered" ? "Delivered" : "Queued securely";
    } catch (error) {
      if (generation !== this.generation) return;
      if (error instanceof ApiUnauthorizedError || (error instanceof ApiHttpError && error.status === 412)) {
        this.clearRecipient();
        this.callbacks.onUnauthorized();
        return;
      }
      this.elements.sendStatus.textContent =
        error instanceof ApiHttpError && error.status === 413 ? "aiyo too long trim it abit" :
        error instanceof ApiHttpError && error.status === 429 ? "wait wait try again ltr" :
        error instanceof ApiNetworkError || error instanceof ApiHttpError ? "aiyo couldnt send try again" :
        "aiyo something broke try again";
    } finally {
      this.busy = false;
      this.elements.sendButton.disabled = false;
    }
  }
}

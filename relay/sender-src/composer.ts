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
  /** Fired after the relay accepts a note, so the page can show it in "recent" right away. */
  onSent?: () => void;
}

/**
 * The relay rejects an envelope whose `createdUtc` is more than an hour old (422). An unchanged
 * retry normally resends the identical envelope so the relay can dedupe it; past this age that
 * resend can only ever fail, so the retry re-encrypts under a fresh id instead.
 */
const STALE_RETRY_ENVELOPE_MS = 50 * 60 * 1000;

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
    // The reaction is part of the note, so a fresh note starts from "none" like the page does,
    // rather than silently carrying the previous note's heart or hug.
    for (const input of this.elements.reactionInputs) {
      input.checked = input.value === "none";
    }
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
      input.addEventListener("change", () => {
        this.invalidateDraft();
        this.updateCounter();
      });
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

  /**
   * Shows the character count against the 2000-character limit, and flags the note as too long
   * when either real limit is exceeded: the character count, or the 4096-byte encoded payload that
   * CJK text and emoji reach well before 2000 characters. The counter used to read "1349 / 2000"
   * for a Chinese note that was already too long to send.
   */
  private updateCounter(): void {
    const text = this.elements.textArea.value;
    const characters = Array.from(text).length;
    const tooLong = characters > MAXIMUM_TEXT_SCALAR_VALUES || this.payloadByteLength(text) > MAXIMUM_PAYLOAD_UTF8_BYTES;
    this.elements.counter.textContent = tooLong
      ? `${characters} / ${MAXIMUM_TEXT_SCALAR_VALUES} too long`
      : `${characters} / ${MAXIMUM_TEXT_SCALAR_VALUES}`;
    this.elements.counter.dataset.overLimit = tooLong ? "true" : "false";
  }

  private payloadByteLength(text: string): number {
    const payload: RemoteMessagePayloadV1 = { kind: "note", text, reaction: this.currentReaction() };
    return new TextEncoder().encode(JSON.stringify(payload)).byteLength;
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
    if (
      this.pendingEnvelope &&
      Date.now() - Date.parse(this.pendingEnvelope.createdUtc) > STALE_RETRY_ENVELOPE_MS
    ) {
      this.pendingEnvelope = null;
    }
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
      this.callbacks.onSent?.();
    } catch (error) {
      if (generation !== this.generation) return;
      if (error instanceof ApiUnauthorizedError || (error instanceof ApiHttpError && error.status === 412)) {
        this.clearRecipient();
        this.callbacks.onUnauthorized();
        return;
      }
      if (error instanceof ApiHttpError && error.status === 422) {
        // The relay refused this exact envelope (for example it is now too old to accept);
        // resending it unchanged can never succeed, so the next tap re-encrypts.
        this.pendingEnvelope = null;
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

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

/** Why the composer gave up on the current pairing: the session is gone (401), or the relay
 * says the desktop's key changed since this browser pinned it (412 `recipient_changed`). */
export type ComposerSessionLoss = "session-lost" | "recipient-changed";

export interface ComposerCallbacks {
  onUnauthorized: (reason: ComposerSessionLoss) => void;
  /** Called after a note is accepted by the relay, so the recent-status list can re-render
   * immediately instead of waiting for the next 30-second status poll. */
  onSent?: () => void;
}

/**
 * UTF-8 length of `text` without materializing its bytes (the counter runs this on every
 * keystroke, and note plaintext should not be copied into more buffers than necessary). A lone
 * surrogate counts as the 3-byte U+FFFD `TextEncoder` would substitute.
 */
function utf8Length(text: string): number {
  let bytes = 0;
  for (const character of text) {
    const codePoint = character.codePointAt(0) ?? 0;
    bytes += codePoint < 0x80 ? 1 : codePoint < 0x800 ? 2 : codePoint < 0x10000 ? 3 : 4;
  }
  return bytes;
}

export interface NoteMeasurement {
  /** Unicode scalar values, the unit `MAXIMUM_TEXT_SCALAR_VALUES` is defined in. */
  scalarValues: number;
  /** UTF-8 size of the whole JSON payload that gets encrypted. */
  payloadBytes: number;
  /** True when either relay/desktop limit would reject this note. */
  tooLong: boolean;
}

/**
 * Measures a note against BOTH limits the send path enforces: the scalar-value count and the
 * encrypted payload's UTF-8 byte budget. The byte budget is the tighter one for non-Latin text
 * (2000 CJK characters are ~6000 bytes), so a counter that only showed "n / 2000" let a note
 * read as fine right up until the send was refused.
 */
export function measureNote(text: string, reaction: Reaction): NoteMeasurement {
  const scalarValues = Array.from(text).length;
  const payload: RemoteMessagePayloadV1 = { kind: "note", text, reaction };
  const payloadBytes = utf8Length(JSON.stringify(payload));
  return {
    scalarValues,
    payloadBytes,
    tooLong: scalarValues > MAXIMUM_TEXT_SCALAR_VALUES || payloadBytes > MAXIMUM_PAYLOAD_UTF8_BYTES,
  };
}

const TOO_LONG_MESSAGE = "aiyo too long trim it abit";

/** `YYYY-MM-DDTHH:MM` in the browser's local time zone, the format `datetime-local` expects. */
function toLocalDateTimeInputValue(date: Date): string {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** A scheduled delivery time, shown back to the sender in their own local time. */
export function formatLocalDeliveryTime(isoUtc: string): string {
  return new Date(isoUtc).toLocaleString(undefined, {
    weekday: "short",
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  });
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
      // A "Queued securely" or "couldnt send" line belongs to the previous attempt; once the
      // sender starts on the next note it is stale. Never clear the in-flight "sending" line.
      if (!this.busy) this.elements.sendStatus.textContent = "";
    });
    for (const input of this.elements.reactionInputs) {
      input.addEventListener("change", () => {
        this.invalidateDraft();
        this.updateCounter();
      });
    }
    this.elements.sendLaterInput.addEventListener("input", () => {
      this.invalidateDraft();
      // "choose a future time" must not linger once the sender has picked a different time.
      this.elements.scheduleStatus.textContent = "";
    });
    this.elements.sendLaterInput.addEventListener("focus", () => this.updateScheduleBounds());
    this.elements.previewButton.addEventListener("click", () => {
      const text = this.elements.textArea.value;
      this.elements.previewText.textContent = text.trim() ? text : "nothing written yet";
      this.elements.previewReaction.textContent = `reaction ${this.currentReaction()}`;
      this.elements.previewDialog.show();
    });
    this.elements.previewClose.addEventListener("click", () => {
      this.clearPreview();
      // The close button is about to be hidden with the dialog; return focus to the control that
      // opened it instead of dropping keyboard/screen-reader users back at the top of the page.
      this.elements.previewButton.focus();
    });
    this.elements.form.addEventListener("submit", (event) => {
      event.preventDefault();
      void this.send();
    });
  }

  private updateCounter(): void {
    const measurement = measureNote(this.elements.textArea.value, this.currentReaction());
    const count = `${measurement.scalarValues} / ${MAXIMUM_TEXT_SCALAR_VALUES}`;
    // Over-limit is spelled out in words (not only the danger color) and flagged on the field.
    this.elements.counter.textContent = measurement.tooLong ? `${count} ${TOO_LONG_MESSAGE}` : count;
    if (measurement.tooLong) {
      this.elements.counter.setAttribute("data-over-limit", "");
      this.elements.textArea.setAttribute("aria-invalid", "true");
    } else {
      this.elements.counter.removeAttribute("data-over-limit");
      this.elements.textArea.removeAttribute("aria-invalid");
    }
  }

  /** Keeps the native picker's range to what the relay accepts: from now to the retention cap. */
  private updateScheduleBounds(): void {
    const now = Date.now();
    this.elements.sendLaterInput.min = toLocalDateTimeInputValue(new Date(now));
    this.elements.sendLaterInput.max = toLocalDateTimeInputValue(new Date(now + MESSAGE_RETENTION_DAYS * 86400_000));
  }

  private currentReaction(): Reaction {
    for (const input of this.elements.reactionInputs) {
      if (input.checked) return input.value as Reaction;
    }
    return "none";
  }

  private resolveDeliverAfterUtc(): string | null | undefined {
    const raw = this.elements.sendLaterInput.value;
    // A half-filled picker (date but no time) reports an empty value; without this check the
    // note would silently go out immediately instead of at the time the sender was choosing.
    if (!raw && this.elements.sendLaterInput.validity?.badInput) {
      this.elements.scheduleStatus.textContent = "finish picking the time or clear it";
      return undefined;
    }
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
      this.elements.sendStatus.textContent = TOO_LONG_MESSAGE;
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
      addRecentStatus(result.messageId, result.status, {
        sentUtc: new Date().toISOString(),
        deliverAfterUtc: envelope.deliverAfterUtc,
      });
      if (revision === this.revision) this.reset();
      this.elements.sendStatus.textContent =
        result.status === "delivered" ? "Delivered" :
        envelope.deliverAfterUtc && Date.parse(envelope.deliverAfterUtc) > Date.now() ? `Scheduled securely for ${formatLocalDeliveryTime(envelope.deliverAfterUtc)}` :
        "Queued securely";
      this.callbacks.onSent?.();
    } catch (error) {
      if (generation !== this.generation) return;
      if (error instanceof ApiUnauthorizedError || (error instanceof ApiHttpError && error.status === 412)) {
        this.clearRecipient();
        this.callbacks.onUnauthorized(error instanceof ApiUnauthorizedError ? "session-lost" : "recipient-changed");
        return;
      }
      if (error instanceof ApiHttpError && error.status === 422 && revision === this.revision) {
        // The relay refused this exact envelope on validation (most often a retry whose
        // createdUtc has aged past the relay's one-hour window). Resending the cached envelope
        // can only fail the same way forever, so the next attempt re-encrypts from the draft.
        this.pendingEnvelope = null;
      }
      this.elements.sendStatus.textContent =
        error instanceof ApiHttpError && error.status === 413 ? TOO_LONG_MESSAGE :
        error instanceof ApiHttpError && error.status === 429 ? "wait wait try again ltr" :
        error instanceof ApiNetworkError || error instanceof ApiHttpError ? "aiyo couldnt send try again" :
        "aiyo something broke try again";
    } finally {
      this.busy = false;
      this.elements.sendButton.disabled = false;
    }
  }
}

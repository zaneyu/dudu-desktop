/**
 * The paired-state composer: message text, reaction, optional schedule, local preview, and the
 * encrypt-then-send flow. Builds a `RemoteMessagePayloadV1`, encrypts it with Task 16's
 * `encryptPayload`, and posts only the resulting `EncryptedEnvelopeV1` — the plaintext payload
 * bytes never reach `fetch`.
 */
import { ApiHttpError, ApiNetworkError, ApiUnauthorizedError, postMessage } from "./api.js";
import { encryptPayload, importRecipientPublicKey, zeroPayloadBytes } from "./crypto.js";
import { addRecentStatus } from "./status.js";
import { MESSAGE_RETENTION_DAYS, type Reaction, type RemoteMessagePayloadV1 } from "../src/protocol/types.js";

const MAX_TEXT_SCALAR_VALUES = 2000;
const MAX_SCHEDULE_AHEAD_MS = MESSAGE_RETENTION_DAYS * 24 * 60 * 60 * 1000;

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
  /** The relay says this browser's session is gone; the caller should drop to unpaired state. */
  onUnauthorized: () => void;
}

export class MessageComposer {
  private publicKeyBase64Url: string | null = null;

  constructor(
    private readonly elements: ComposerElements,
    private readonly callbacks: ComposerCallbacks,
  ) {
    this.wire();
    this.updateCounter();
  }

  setRecipientPublicKey(publicKeyBase64Url: string): void {
    this.publicKeyBase64Url = publicKeyBase64Url;
  }

  reset(): void {
    this.elements.textArea.value = "";
    this.elements.sendLaterInput.value = "";
    this.elements.scheduleStatus.textContent = "";
    this.updateCounter();
  }

  private wire(): void {
    this.elements.textArea.addEventListener("input", () => this.updateCounter());
    this.elements.previewButton.addEventListener("click", () => this.showPreview());
    this.elements.previewClose.addEventListener("click", () => this.elements.previewDialog.close());
    this.elements.form.addEventListener("submit", (event) => {
      event.preventDefault();
      void this.send();
    });
  }

  private updateCounter(): void {
    const length = countUnicodeScalarValues(this.elements.textArea.value);
    this.elements.counter.textContent = `${length} / ${MAX_TEXT_SCALAR_VALUES}`;
  }

  private currentReaction(): Reaction {
    for (const input of this.elements.reactionInputs) {
      if (input.checked) {
        return input.value as Reaction;
      }
    }
    return "none";
  }

  private showPreview(): void {
    this.elements.previewText.textContent = this.elements.textArea.value;
    this.elements.previewReaction.textContent = `reaction ${this.currentReaction()}`;
    // Non-modal on purpose: this is a local preview, not a blocking confirmation step, so the
    // rest of the composer (including Send note) stays reachable while it is open.
    this.elements.previewDialog.show();
  }

  private setBusy(busy: boolean): void {
    this.elements.sendButton.disabled = busy;
  }

  /** Converts the `Send later` local date/time value to an ISO UTC timestamp, validated against
   * the relay's scheduling cap. Returns undefined (and writes a voice error line) when the value
   * is present but invalid; the caller should abort the send in that case. */
  private resolveDeliverAfterUtc(): string | null | undefined {
    const rawValue = this.elements.sendLaterInput.value;
    if (!rawValue) {
      this.elements.scheduleStatus.textContent = "";
      return null;
    }
    const scheduledMs = new Date(rawValue).getTime();
    if (Number.isNaN(scheduledMs)) {
      this.elements.scheduleStatus.textContent = "that time didnt make sense";
      return undefined;
    }
    if (scheduledMs - Date.now() > MAX_SCHEDULE_AHEAD_MS) {
      this.elements.scheduleStatus.textContent = "cant schedule that far ahead";
      return undefined;
    }
    this.elements.scheduleStatus.textContent = "";
    return new Date(scheduledMs).toISOString();
  }

  private async send(): Promise<void> {
    if (!this.publicKeyBase64Url) {
      return;
    }

    const deliverAfterUtc = this.resolveDeliverAfterUtc();
    if (deliverAfterUtc === undefined) {
      return;
    }

    const text = this.elements.textArea.value;
    const payload: RemoteMessagePayloadV1 = { kind: "note", text, reaction: this.currentReaction() };
    const payloadBytes = new TextEncoder().encode(JSON.stringify(payload));

    this.setBusy(true);
    this.elements.sendStatus.textContent = "sending";

    try {
      const recipientKey = await importRecipientPublicKey(this.publicKeyBase64Url);
      let envelope;
      try {
        envelope = await encryptPayload(recipientKey, payload, {
          messageId: crypto.randomUUID(),
          deliverAfterUtc,
        });
      } finally {
        zeroPayloadBytes(payloadBytes);
      }

      const result = await postMessage(envelope);
      addRecentStatus(result.messageId, result.status);
      this.reset();
      this.elements.sendStatus.textContent = "Queued securely";
    } catch (error) {
      if (error instanceof ApiUnauthorizedError) {
        this.callbacks.onUnauthorized();
        return;
      }
      if (error instanceof ApiNetworkError || error instanceof ApiHttpError) {
        this.elements.sendStatus.textContent = "aiyo couldnt send try again";
      } else {
        this.elements.sendStatus.textContent = "aiyo something broke try again";
      }
    } finally {
      this.setBusy(false);
    }
  }
}

function countUnicodeScalarValues(text: string): number {
  let count = 0;
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  for (const _character of text) {
    count++;
  }
  return count;
}

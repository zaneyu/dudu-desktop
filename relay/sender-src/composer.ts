/**
 * The paired-state composer: message text, reaction, optional schedule, local preview, and the
 * encrypt-then-send flow. Builds a `RemoteMessagePayloadV1`, encodes it to UTF-8 JSON bytes once,
 * encrypts those exact bytes with Task 16's `encryptPayloadBytes`, and posts only the resulting
 * `EncryptedEnvelopeV1` — the plaintext payload bytes never reach `fetch`, and the same buffer
 * that was fed to AES-GCM is zeroed afterward.
 */
import { ApiHttpError, ApiNetworkError, ApiUnauthorizedError, postMessage } from "./api.js";
import { encryptPayloadBytes, importRecipientPublicKey, zeroPayloadBytes } from "./crypto.js";
import { addRecentStatus } from "./status.js";
import {
  MAXIMUM_PAYLOAD_UTF8_BYTES,
  MAXIMUM_TEXT_SCALAR_VALUES,
  MESSAGE_RETENTION_DAYS,
  type Reaction,
  type RemoteMessagePayloadV1,
} from "../src/protocol/types.js";

const MAX_TEXT_SCALAR_VALUES = MAXIMUM_TEXT_SCALAR_VALUES;
const MAX_SCHEDULE_AHEAD_MS = MESSAGE_RETENTION_DAYS * 24 * 60 * 60 * 1000;
const TOO_LONG_STATUS = "aiyo too long trim it abit";
const RATE_LIMITED_STATUS = "wait wait try again ltr";
const SEND_FAILED_STATUS = "aiyo couldnt send try again";
const UNEXPECTED_ERROR_STATUS = "aiyo something broke try again";

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
  /** Minted once per draft and reused across retries so the relay's same-ID dedup collapses a
   * retried send onto the original, instead of a `fetch` failure after the relay already queued
   * the message producing a second, duplicate queued note. Cleared (forcing a fresh id) after a
   * successful send or whenever the user clears the message text — see `wire()`. */
  private draftMessageId: string | null = null;

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
    this.draftMessageId = null;
    this.updateCounter();
  }

  private wire(): void {
    this.elements.textArea.addEventListener("input", () => {
      // An emptied textarea means the user has abandoned this draft (or already sent it and
      // started a new one): the next send should mint a fresh messageId, not reuse a stale one.
      if (this.elements.textArea.value === "") {
        this.draftMessageId = null;
      }
      this.updateCounter();
    });
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

    if (payloadBytes.byteLength > MAXIMUM_PAYLOAD_UTF8_BYTES) {
      this.elements.sendStatus.textContent = TOO_LONG_STATUS;
      return;
    }

    // Reused across retries of this same draft; only cleared on success or when the user clears
    // the text (see `wire()` and `reset()`), so a retried send dedups against the relay's
    // same-ID-from-same-sender idempotency instead of queuing a duplicate note.
    if (!this.draftMessageId) {
      this.draftMessageId = crypto.randomUUID();
    }
    const messageId = this.draftMessageId;

    this.setBusy(true);
    this.elements.sendStatus.textContent = "sending";

    try {
      const recipientKey = await importRecipientPublicKey(this.publicKeyBase64Url);
      let envelope;
      try {
        envelope = await encryptPayloadBytes(recipientKey, payloadBytes, {
          messageId,
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
      if (error instanceof ApiHttpError && error.status === 413) {
        this.elements.sendStatus.textContent = TOO_LONG_STATUS;
      } else if (error instanceof ApiHttpError && error.status === 429) {
        this.elements.sendStatus.textContent = RATE_LIMITED_STATUS;
      } else if (error instanceof ApiNetworkError || error instanceof ApiHttpError) {
        this.elements.sendStatus.textContent = SEND_FAILED_STATUS;
      } else {
        this.elements.sendStatus.textContent = UNEXPECTED_ERROR_STATUS;
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

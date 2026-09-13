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
  /** Minted once per draft and reused only across an *identical* retry, so the relay's same-ID
   * dedup collapses a retried send onto the original instead of producing a second, duplicate
   * queued note. Must NOT survive any change to what would actually be sent: the relay dedups
   * on (sender, messageId) alone, so reusing the id across an edited retry would make the relay
   * silently keep the pre-edit content and report success, discarding the user's edit. Cleared
   * (forcing a fresh id on the next send) on every textarea input, reaction change, or
   * send-later change, as well as after a successful send — see `wire()` and `reset()`. */
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
      // Any edit to the text changes what would actually be sent, so the id must not survive
      // it -- otherwise a retry after an edit would reuse the id from the pre-edit attempt and
      // the relay's same-ID dedup would silently keep the stale content. Only an identical
      // retry (nothing touched between attempts) should reuse `draftMessageId`.
      this.draftMessageId = null;
      this.updateCounter();
    });
    for (const reactionInput of this.elements.reactionInputs) {
      reactionInput.addEventListener("change", () => {
        this.draftMessageId = null;
      });
    }
    this.elements.sendLaterInput.addEventListener("input", () => {
      this.draftMessageId = null;
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

    // Reused only when nothing about the draft changed since the last attempt (see `wire()`,
    // which clears it on every text/reaction/schedule edit, and `reset()` on success), so an
    // identical retry dedups against the relay's same-ID idempotency instead of queuing a
    // duplicate note, while an edited retry always gets a fresh id and is never silently
    // swallowed by that same dedup.
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

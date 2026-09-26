/**
 * In-browser record of recently sent message ids and their delivery status, capped at 20 and
 * holding no note text — only `messageId` and `status` ever get written here. Backed by
 * `sessionStorage` (not `localStorage`) so it does not outlive the tab.
 */
import { ApiUnauthorizedError, getMessageStatus } from "./api.js";
import type { MessageState } from "../src/protocol/types.js";

const STORAGE_KEY = "dudu.sender.recent.v1";
const MAX_RECENT = 20;
const POLL_INTERVAL_MS = 30_000;

export interface RecentStatus {
  messageId: string;
  status: MessageState | "unavailable";
  /** When this browser sent it; optional so entries written before this field still load. */
  sentAtMs?: number | null;
}

function isRecentStatus(value: unknown): value is RecentStatus {
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const candidate = value as Record<string, unknown>;
  return (
    typeof candidate.messageId === "string" &&
    typeof candidate.status === "string" &&
    (candidate.sentAtMs === undefined || candidate.sentAtMs === null || typeof candidate.sentAtMs === "number")
  );
}

function loadRecent(): RecentStatus[] {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return [];
    }
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter(isRecentStatus) : [];
  } catch {
    return [];
  }
}

function saveRecent(entries: RecentStatus[]): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(entries.slice(0, MAX_RECENT)));
  } catch {
    // Storage full or blocked (private mode): the list is a convenience, never worth failing a
    // send over after the relay already accepted the note.
  }
}

export function addRecentStatus(messageId: string, status: MessageState, sentAtMs = Date.now()): void {
  const entries = loadRecent().filter((entry) => entry.messageId !== messageId);
  entries.unshift({ messageId, status, sentAtMs });
  saveRecent(entries);
}

export function clearRecent(): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Blocked storage has nothing to clear.
  }
}

function sentAtLabel(sentAtMs: number | null | undefined): string {
  if (typeof sentAtMs !== "number" || !Number.isFinite(sentAtMs)) {
    return "";
  }
  return new Date(sentAtMs).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
}

function statusLine(status: RecentStatus["status"]): string {
  switch (status) {
    case "queued":
      return "on the way";
    case "delivered":
      return "delivered le";
    case "expired":
      return "expired";
    case "unavailable":
      return "status no longer available";
    default:
      return status;
  }
}

/** Renders the recent-status list and polls `GET /v1/messages/:id/status` for anything still
 * queued, every 30s while the tab is visible. */
export class StatusTracker {
  private timer: ReturnType<typeof setInterval> | null = null;
  private polling = false;

  constructor(
    private readonly listElement: HTMLElement,
    private readonly onUnauthorized: () => void,
  ) {}

  render(): void {
    const entries = loadRecent();
    this.listElement.replaceChildren(
      ...entries.map((entry) => {
        const item = document.createElement("li");
        // Without the send time every entry read the same ("on the way", "on the way"), so there
        // was no telling which note a status belonged to.
        const sentAt = sentAtLabel(entry.sentAtMs);
        item.textContent = sentAt ? `${sentAt} · ${statusLine(entry.status)}` : statusLine(entry.status);
        return item;
      }),
    );
  }

  /** Starts polling, checking once immediately so a returning visit does not wait 30s. */
  start(): void {
    this.stop();
    this.timer = setInterval(() => void this.poll(), POLL_INTERVAL_MS);
    void this.poll();
  }

  /** Re-renders now and checks queued statuses once, e.g. after a send or on tab return. */
  refresh(): void {
    if (this.timer === null) {
      return;
    }
    this.render();
    void this.poll();
  }

  stop(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  private async poll(): Promise<void> {
    if (document.visibilityState !== "visible" || this.polling) {
      return;
    }
    this.polling = true;
    try {
      await this.pollQueued();
    } finally {
      this.polling = false;
    }
  }

  private async pollQueued(): Promise<void> {
    const queued = loadRecent().filter((entry) => entry.status === "queued");
    for (const entry of queued) {
      if (this.timer === null) {
        return;
      }
      try {
        const result = await getMessageStatus(entry.messageId);
        this.applyStatus(entry.messageId, result?.status ?? "unavailable");
      } catch (error) {
        if (error instanceof ApiUnauthorizedError) {
          this.onUnauthorized();
          return;
        }
        // Any other failure: leave the status as-is, the next tick tries again.
      }
    }
  }

  private applyStatus(messageId: string, status: RecentStatus["status"]): void {
    const entries = loadRecent();
    const index = entries.findIndex((entry) => entry.messageId === messageId);
    if (index === -1) {
      return;
    }
    entries[index] = { ...entries[index], messageId, status };
    saveRecent(entries);
    this.render();
  }
}

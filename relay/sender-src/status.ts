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
}

function isRecentStatus(value: unknown): value is RecentStatus {
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const candidate = value as Record<string, unknown>;
  return typeof candidate.messageId === "string" && typeof candidate.status === "string";
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
  sessionStorage.setItem(STORAGE_KEY, JSON.stringify(entries.slice(0, MAX_RECENT)));
}

export function addRecentStatus(messageId: string, status: MessageState): void {
  const entries = loadRecent().filter((entry) => entry.messageId !== messageId);
  entries.unshift({ messageId, status });
  saveRecent(entries);
}

export function clearRecent(): void {
  sessionStorage.removeItem(STORAGE_KEY);
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

  constructor(
    private readonly listElement: HTMLElement,
    private readonly onUnauthorized: () => void,
  ) {}

  render(): void {
    const entries = loadRecent();
    this.listElement.replaceChildren(
      ...entries.map((entry) => {
        const item = document.createElement("li");
        item.textContent = statusLine(entry.status);
        return item;
      }),
    );
  }

  start(): void {
    this.stop();
    this.timer = setInterval(() => void this.poll(), POLL_INTERVAL_MS);
  }

  stop(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  private async poll(): Promise<void> {
    if (document.visibilityState !== "visible") {
      return;
    }
    const queued = loadRecent().filter((entry) => entry.status === "queued");
    for (const entry of queued) {
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
    entries[index] = { messageId, status };
    saveRecent(entries);
    this.render();
  }
}

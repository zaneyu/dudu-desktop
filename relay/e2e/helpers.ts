import { expect, type Page } from "@playwright/test";

import { mockRelay, VALID_PAIRING_CODE, type MockRelay } from "./mock-relay.js";

/** Navigates to the sender page, pairs with the mock relay's valid code, and waits for the
 * paired-state composer to be visible. Returns the mock relay handle so the caller can inspect
 * intercepted requests. */
export async function openPairedSender(page: Page): Promise<MockRelay> {
  const api = await mockRelay(page);
  await page.goto("/");
  await page.getByLabel("Pairing code").fill(VALID_PAIRING_CODE);
  await page.getByRole("button", { name: "Pair privately" }).click();
  await expect(page.getByLabel("Message")).toBeVisible();
  return api;
}

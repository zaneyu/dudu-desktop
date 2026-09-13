import { expect, test } from "@playwright/test";

import { openPairedSender } from "./helpers.js";
import { mockRelay, VALID_PAIRING_CODE } from "./mock-relay.js";

test("disconnect removes the paired composer", async ({ page }) => {
  await openPairedSender(page);
  await page.getByRole("button", { name: "Disconnect this phone" }).click();
  await expect(page.getByRole("button", { name: "Pair privately" })).toBeVisible();
});

test("pairs and sends a note using only the keyboard", async ({ page }) => {
  const api = await mockRelay(page);
  await page.goto("/");

  await page.getByLabel("Pairing code").focus();
  await page.keyboard.type(VALID_PAIRING_CODE);
  await page.keyboard.press("Enter");

  await expect(page.getByLabel("Message")).toBeVisible();

  await page.getByLabel("Message").focus();
  await page.keyboard.type("keyboard only note");

  // Tab past the reaction radios and the optional schedule field to the Send note button.
  await page.getByRole("button", { name: "Send note" }).focus();
  await page.keyboard.press("Enter");

  await expect(page.getByTestId("send-status")).toHaveText("Queued securely");
  expect(api.lastDecryptedPayload?.text).toBe("keyboard only note");
});

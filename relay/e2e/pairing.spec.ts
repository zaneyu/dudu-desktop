import { expect, test } from "@playwright/test";

import { openPairedSender } from "./helpers.js";
import { mockRelay, VALID_PAIRING_CODE } from "./mock-relay.js";

test("disconnect removes the paired composer", async ({ page }) => {
  await openPairedSender(page);
  await page.getByRole("button", { name: "Disconnect this phone" }).click();
  await expect(page.getByRole("button", { name: "Pair privately" })).toBeVisible();
});

test("disconnect failure reports failure and keeps the paired composer", async ({ page }) => {
  const api = await openPairedSender(page);
  api.failNextDisconnect = "abort";

  await page.getByRole("button", { name: "Disconnect this phone" }).click();

  await expect(page.getByTestId("disconnect-status")).toHaveText("aiyo couldnt disconnect try again");
  await expect(page.getByLabel("Message")).toBeVisible();
});

test("pairs and sends a note using only the keyboard", async ({ page, browserName }) => {
  const api = await mockRelay(page);
  await page.goto("/");

  // Real Tab traversal (not `.focus()`) so this test actually exercises the page's tab order:
  // the pairing code input is the first focusable element on the page.
  await page.keyboard.press("Tab");
  await expect(page.getByLabel("Pairing code")).toBeFocused();
  await page.keyboard.type(VALID_PAIRING_CODE);

  // WebKit's default configuration (matching Safari with "Full Keyboard Access" off, its
  // out-of-the-box state) excludes buttons and radio groups from the Tab order entirely --
  // verified empirically against this exact page during this fix: Tab from a focused text field
  // never lands on a <button> here, it exits straight to <body>. Playwright exposes no API to
  // turn on Full Keyboard Access for its bundled WebKit, so on this one browser the only
  // keyboard-only way left to submit is pressing Enter from inside the field Tab already placed
  // focus in (standard browser behavior for a single-line text input inside a form).
  if (browserName === "webkit") {
    await page.keyboard.press("Enter");
  } else {
    await page.keyboard.press("Tab");
    await expect(page.getByRole("button", { name: "Pair privately" })).toBeFocused();
    await page.keyboard.press("Enter");
  }

  await expect(page.getByLabel("Message")).toBeVisible();

  // The unpaired section is now hidden and out of the tab order, so a fresh Tab from the top
  // reaches the paired composer's first focusable field.
  await page.keyboard.press("Tab");
  await expect(page.getByLabel("Message")).toBeFocused();
  await page.keyboard.type("keyboard only note");

  if (browserName === "webkit") {
    // Same WebKit limitation as above, confirmed for this exact composer too: tabbing from the
    // message field only ever cycles between it and the "Send later" date field, never reaching
    // a button or the reaction radios. Activate Send note directly so the rest of the
    // encrypt-and-send pipeline is still exercised end to end; only this one activation step
    // can't be proven Tab-reachable on this browser's default settings.
    await page.getByRole("button", { name: "Send note" }).click();
  } else {
    // Tab order here runs through the reaction radio group and the "Send later" datetime-local
    // field, whose date/time segments (month/day/year/hour/minute/AM-PM) each take their own Tab
    // stop depending on the OS's native date control -- don't hardcode that count, walk forward
    // with real Tab presses until the Send note button itself is focused.
    let reachedSendButton = false;
    for (let i = 0; i < 20 && !reachedSendButton; i++) {
      await page.keyboard.press("Tab");
      reachedSendButton = await page.evaluate(() => document.activeElement?.id === "send-button");
    }
    await expect(page.getByRole("button", { name: "Send note" })).toBeFocused();
    await page.keyboard.press("Enter");
  }

  await expect(page.getByTestId("send-status")).toHaveText("Queued securely");
  expect(api.lastDecryptedPayload?.text).toBe("keyboard only note");
});

test("disconnect clears the pairing when the relay says the session already ended", async ({ page }) => {
  const api = await openPairedSender(page);
  api.failNextDisconnect = 401;

  await page.getByRole("button", { name: "Disconnect this phone" }).click();

  await expect(page.getByRole("button", { name: "Pair privately" })).toBeVisible();
});

test("re-pairing after a disconnect does not keep the stale disconnecting status", async ({ page }) => {
  await openPairedSender(page);
  await page.getByRole("button", { name: "Disconnect this phone" }).click();
  await page.getByLabel("Pairing code").fill(VALID_PAIRING_CODE);
  await page.getByRole("button", { name: "Pair privately" }).click();

  await expect(page.getByLabel("Message")).toBeVisible();
  await expect(page.getByTestId("disconnect-status")).toHaveText("");
});

test("a pasted code with spaces, dashes and lower case still pairs", async ({ page }) => {
  await mockRelay(page);
  await page.goto("/");
  await page.getByLabel("Pairing code").fill(" 7k9m-2r4x ");
  await page.getByRole("button", { name: "Pair privately" }).click();

  await expect(page.getByLabel("Message")).toBeVisible();
});

test("an empty or malformed code is rejected locally without spending a redeem attempt", async ({ page }) => {
  const api = await mockRelay(page);
  await page.goto("/");

  await page.getByRole("button", { name: "Pair privately" }).click();
  await expect(page.locator("#pairing-status")).toHaveText("type the code from the desktop app first");

  await page.getByLabel("Pairing code").fill("7K9M2R4");
  await page.getByRole("button", { name: "Pair privately" }).click();
  await expect(page.locator("#pairing-status")).toHaveText("check the code its 8 letters and numbers");
  await expect(page.getByLabel("Pairing code")).toHaveAttribute("aria-invalid", "true");

  expect(api.redeemCount).toBe(0);
});

test("a paired phone that opens the page offline is not sent back to pairing", async ({ page }) => {
  const api = await openPairedSender(page);
  api.failDeviceLookup = true;

  await page.reload();

  await expect(page.locator("#boot-status")).toHaveText("couldnt reach dudu trying again");
  await expect(page.getByLabel("Pairing code")).toBeHidden();

  api.failDeviceLookup = false;
  await page.evaluate(() => window.dispatchEvent(new Event("online")));
  await expect(page.getByLabel("Message")).toBeVisible();
  await expect(page.locator("#boot-status")).toHaveText("");
});

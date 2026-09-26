/**
 * Browser-level regressions for sender-page UI/UX bugs: pairing-code cleanup and busy state,
 * focus management between the two states, confirmation before disconnecting, stale status
 * lines, the recent list updating after a send, the key-changed path on a 412, the offline
 * load path, and phone layout (no horizontal overflow at 320px, no iOS focus zoom).
 */
import { expect, test } from "@playwright/test";

import { openPairedSender } from "./helpers.js";
import { mockRelay, VALID_PAIRING_CODE } from "./mock-relay.js";

test("a pasted code with a dash, spaces, and lowercase is cleaned up and pairs", async ({ page }) => {
  const api = await mockRelay(page);
  await page.goto("/");
  const input = page.getByLabel("Pairing code");
  await input.fill(` ${VALID_PAIRING_CODE.slice(0, 4).toLowerCase()}-${VALID_PAIRING_CODE.slice(4).toLowerCase()} `);
  await expect(input).toHaveValue(VALID_PAIRING_CODE);

  await page.getByRole("button", { name: "Pair privately" }).click();
  await expect(page.getByLabel("Message")).toBeVisible();
  expect(api.redeemCount).toBe(1);
});

test("a malformed code is caught locally without spending a relay attempt", async ({ page }) => {
  const api = await mockRelay(page);
  await page.goto("/");
  await page.getByLabel("Pairing code").fill("7K9M");
  await page.getByRole("button", { name: "Pair privately" }).click();

  await expect(page.locator("#pairing-status")).toHaveText("the code is 8 letters and numbers check it again");
  await expect(page.getByLabel("Pairing code")).toHaveAttribute("aria-invalid", "true");
  expect(api.redeemCount).toBe(0);

  // Editing the code clears the error for the next attempt.
  await page.getByLabel("Pairing code").pressSequentially("2R4X");
  await expect(page.locator("#pairing-status")).toHaveText("");
  await expect(page.getByLabel("Pairing code")).not.toHaveAttribute("aria-invalid", "true");
});

test("a wrong code does not claim the code expired", async ({ page }) => {
  await mockRelay(page);
  await page.goto("/");
  await page.getByLabel("Pairing code").fill("ABCDEFGH");
  await page.getByRole("button", { name: "Pair privately" }).click();
  await expect(page.locator("#pairing-status")).toHaveText("aiyo that code didnt work or expired check it or get a new one");
});

test("pairing disables the button while the redemption is in flight and sends it once", async ({ page }) => {
  const api = await mockRelay(page);
  api.redeemDelayMs = 400;
  await page.goto("/");
  await page.getByLabel("Pairing code").fill(VALID_PAIRING_CODE);
  const pairButton = page.getByRole("button", { name: "Pair privately" });
  await pairButton.click();
  await expect(pairButton).toBeDisabled();
  // A second submit (Enter in the field) while busy must be ignored.
  await page.getByLabel("Pairing code").press("Enter");

  await expect(page.getByLabel("Message")).toBeVisible();
  expect(api.redeemCount).toBe(1);
});

test("dismissing the disconnect confirmation keeps the phone paired", async ({ page }) => {
  await openPairedSender(page);
  let prompted = false;
  page.once("dialog", (dialog) => {
    prompted = true;
    void dialog.dismiss();
  });
  await page.getByRole("button", { name: "Disconnect this phone" }).click();

  expect(prompted).toBe(true);
  await expect(page.getByLabel("Message")).toBeVisible();
  await expect(page.getByTestId("disconnect-status")).toHaveText("");
});

test("after disconnecting, the page says so, focuses the code field, and a re-pair starts clean", async ({
  page,
}) => {
  const api = await openPairedSender(page);
  api.failNextDisconnect = "abort";
  page.on("dialog", (dialog) => void dialog.accept());
  await page.getByRole("button", { name: "Disconnect this phone" }).click();
  await expect(page.getByTestId("disconnect-status")).toHaveText("aiyo couldnt disconnect try again");

  await page.getByRole("button", { name: "Disconnect this phone" }).click();
  await expect(page.locator("#pairing-status")).toHaveText("disconnected le get a new code to pair again");
  await expect(page.getByLabel("Pairing code")).toBeFocused();

  await page.getByLabel("Pairing code").fill(VALID_PAIRING_CODE);
  await page.getByRole("button", { name: "Pair privately" }).click();
  await expect(page.getByLabel("Message")).toBeFocused();
  // Neither the old failure nor a stale "disconnecting" line may carry over into the new pairing.
  await expect(page.getByTestId("disconnect-status")).toHaveText("");
});

test("the recent list shows a sent note right away and hides while empty", async ({ page }) => {
  await openPairedSender(page);
  await expect(page.getByRole("heading", { name: "recent" })).toBeHidden();

  await page.getByLabel("Message").fill("show me in recent");
  await page.getByRole("button", { name: "Send note" }).click();
  await expect(page.getByTestId("send-status")).toHaveText("Queued securely");

  await expect(page.getByRole("heading", { name: "recent" })).toBeVisible();
  await expect(page.locator("#recent-statuses li")).toHaveCount(1);
  await expect(page.locator("#recent-statuses li").first()).toContainText("on the way");
  await expect(page.locator("#recent-statuses li").first()).toContainText("sent ");
});

test("the result line clears once the next note is started", async ({ page }) => {
  await openPairedSender(page);
  await page.getByLabel("Message").fill("first");
  await page.getByRole("button", { name: "Send note" }).click();
  await expect(page.getByTestId("send-status")).toHaveText("Queued securely");

  await page.getByLabel("Message").pressSequentially("n");
  await expect(page.getByTestId("send-status")).toHaveText("");
});

test("a message longer than 1000 emoji is not truncated and the counter matches the limit", async ({ page }) => {
  await openPairedSender(page);
  const note = "💜".repeat(1200);
  await page.getByLabel("Message").fill(note);
  await expect(page.getByLabel("Message")).toHaveValue(note);
  await expect(page.locator("#char-counter")).toHaveText("1200 / 2000 aiyo too long trim it abit");
  await expect(page.getByLabel("Message")).toHaveAttribute("aria-invalid", "true");
});

test("a recipient key change while sending says the key changed, not that the phone disconnected", async ({
  page,
}) => {
  const api = await openPairedSender(page);
  await page.getByLabel("Message").fill("about to hit a rotated key");
  api.failNextSend = 412;
  await page.getByRole("button", { name: "Send note" }).click();

  await expect(page.locator("#pairing-status")).toHaveText("dudu key changed pair again pls");
  await expect(page.getByLabel("Pairing code")).toBeFocused();
  expect(await page.evaluate(() => localStorage.getItem("dudu.sender.device.v1"))).toBeNull();
});

test("a paired phone that reloads stays paired", async ({ page }) => {
  await openPairedSender(page);
  await page.reload();
  await expect(page.getByLabel("Message")).toBeVisible();
  await expect(page.getByRole("button", { name: "Pair privately" })).toBeHidden();
});

test("an unreachable relay on load explains itself and recovers when back online", async ({ page, context }) => {
  const api = await openPairedSender(page);
  api.failDeviceCheck = true;
  await page.reload();

  await expect(page.locator("#pairing-status")).toHaveText(
    "couldnt reach dudu check your connection ill retry when youre back online",
  );
  // The cached pairing survives the failed check.
  expect(await page.evaluate(() => localStorage.getItem("dudu.sender.device.v1"))).not.toBeNull();

  api.failDeviceCheck = false;
  await context.setOffline(true);
  await context.setOffline(false);
  await expect(page.getByLabel("Message")).toBeVisible();
});

test("the page fits a 320px phone without horizontal scrolling", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 640 });
  await openPairedSender(page);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
});

test("text fields use at least 16px text so iOS does not zoom on focus", async ({ page }) => {
  await openPairedSender(page);
  for (const id of ["message-text", "send-later"]) {
    const size = await page.locator(`#${id}`).evaluate((element) => parseFloat(getComputedStyle(element).fontSize));
    expect(size).toBeGreaterThanOrEqual(16);
  }
});

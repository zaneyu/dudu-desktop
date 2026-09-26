import { expect, test } from "@playwright/test";

import { mockRelay } from "./mock-relay.js";
import { openPairedSender } from "./helpers.js";

test("pairs, previews locally, and sends ciphertext without leaking note text", async ({ page }) => {
  const api = await mockRelay(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await page.getByLabel("Pairing code").fill("7K9M2R4X");
  await page.getByRole("button", { name: "Pair privately" }).click();
  await page.getByLabel("Message").fill("I love you, good luck today");
  await page.getByRole("button", { name: "Preview" }).click();
  await expect(page.getByTestId("preview")).toContainText("I love you");
  await page.getByRole("button", { name: "Send note" }).click();

  // Wait for the send to actually complete (the mock relay records the request asynchronously)
  // before inspecting what the mock captured.
  await expect(page.getByTestId("send-status")).toHaveText("Queued securely");
  expect(api.lastRequestBody).not.toContain("I love you");
  expect(api.lastDecryptedPayload?.text).toBe("I love you, good luck today");
});

test("send button has no transition duration when reduced motion is requested", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await mockRelay(page);
  await page.goto("/");
  await page.getByLabel("Pairing code").fill("7K9M2R4X");
  await page.getByRole("button", { name: "Pair privately" }).click();
  await expect(page.getByLabel("Message")).toBeVisible();

  const sendButton = page.getByRole("button", { name: "Send note" });
  const transitionDuration = await sendButton.evaluate((element) => getComputedStyle(element).transitionDuration);
  expect(transitionDuration).toBe("0s");
});

test("a dropped connection or 5xx keeps the note text and shows a failure status", async ({ page }) => {
  const api = await openPairedSender(page);
  await page.getByLabel("Message").fill("still here if it fails");

  api.failNextSend = "abort";
  await page.getByRole("button", { name: "Send note" }).click();
  await expect(page.getByTestId("send-status")).toHaveText("aiyo couldnt send try again");
  await expect(page.getByLabel("Message")).toHaveValue("still here if it fails");
  await expect(page.getByRole("button", { name: "Send note" })).toBeEnabled();

  api.failNextSend = 500;
  await page.getByRole("button", { name: "Send note" }).click();
  await expect(page.getByTestId("send-status")).toHaveText("aiyo couldnt send try again");
  await expect(page.getByLabel("Message")).toHaveValue("still here if it fails");
  expect(api.lastDecryptedPayload).toBeNull();

  // Nothing about the draft changed between the two attempts, so both should have gone out
  // under the same messageId -- an identical retry must dedup against the relay, not queue twice.
  expect(api.sentMessageIds).toHaveLength(2);
  expect(api.sentMessageIds[0]).toBe(api.sentMessageIds[1]);
});

test("a 401 mid-session drops the page back to the unpaired state", async ({ page }) => {
  const api = await openPairedSender(page);
  await page.getByLabel("Message").fill("about to lose the session");

  api.failNextSend = 401;
  await page.getByRole("button", { name: "Send note" }).click();

  await expect(page.getByRole("button", { name: "Pair privately" })).toBeVisible();
  await expect(page.getByText("phone disconnected pair again")).toBeVisible();
});

test("editing the note after a failed send mints a new message id instead of reusing the stale one", async ({
  page,
}) => {
  const api = await openPairedSender(page);
  await page.getByLabel("Message").fill("first attempt, before the edit");

  api.failNextSend = "abort";
  await page.getByRole("button", { name: "Send note" }).click();
  await expect(page.getByTestId("send-status")).toHaveText("aiyo couldnt send try again");

  // The user edits the text rather than clearing it -- the retry must NOT reuse the first
  // attempt's messageId, or the relay's same-ID dedup would silently keep the pre-edit content
  // and report success while discarding the edit.
  await page.getByLabel("Message").fill("second attempt, after the edit");
  await page.getByRole("button", { name: "Send note" }).click();
  await expect(page.getByTestId("send-status")).toHaveText("Queued securely");

  expect(api.lastDecryptedPayload?.text).toBe("second attempt, after the edit");
  expect(api.sentMessageIds).toHaveLength(2);
  expect(api.sentMessageIds[0]).not.toBe(api.sentMessageIds[1]);
});

test("a sent note shows up in recent right away", async ({ page }) => {
  await openPairedSender(page);
  await page.getByLabel("Message").fill("show me in recent");
  await page.getByRole("button", { name: "Send note" }).click();

  await expect(page.getByTestId("send-status")).toHaveText("Queued securely");
  await expect(page.locator("#recent-statuses li")).toHaveCount(1);
  await expect(page.locator("#recent-statuses li").first()).toContainText("on the way");
});

test("the preview keeps line breaks and wraps long words on a phone", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openPairedSender(page);
  await page.getByLabel("Message").fill(`first line\nsecond line ${"x".repeat(120)}`);
  await page.getByRole("button", { name: "Preview" }).click();

  const preview = page.getByTestId("preview");
  expect(await preview.evaluate((element) => (element as HTMLElement).innerText)).toContain("\n");
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
});

test("the counter flags a note that is too long in bytes before the character limit", async ({ page }) => {
  await openPairedSender(page);
  await page.getByLabel("Message").fill("爱".repeat(1400));

  await expect(page.locator("#char-counter")).toHaveText("1400 / 2000 too long");
});

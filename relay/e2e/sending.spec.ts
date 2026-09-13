import { expect, test } from "@playwright/test";

import { mockRelay } from "./mock-relay.js";

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

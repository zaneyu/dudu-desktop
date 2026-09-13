import { defineConfig } from "@playwright/test";

const PHONE_VIEWPORT = { width: 390, height: 844 };
const DESKTOP_VIEWPORT = { width: 1280, height: 800 };

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: true,
  reporter: "list",
  use: {
    baseURL: "http://127.0.0.1:4173",
  },
  webServer: {
    command: "node e2e/static-server.mjs",
    port: 4173,
    reuseExistingServer: !process.env.CI,
  },
  projects: [
    { name: "chromium-phone", use: { browserName: "chromium", viewport: PHONE_VIEWPORT } },
    { name: "chromium-desktop", use: { browserName: "chromium", viewport: DESKTOP_VIEWPORT } },
    { name: "firefox-phone", use: { browserName: "firefox", viewport: PHONE_VIEWPORT } },
    { name: "firefox-desktop", use: { browserName: "firefox", viewport: DESKTOP_VIEWPORT } },
    { name: "webkit-phone", use: { browserName: "webkit", viewport: PHONE_VIEWPORT } },
    { name: "webkit-desktop", use: { browserName: "webkit", viewport: DESKTOP_VIEWPORT } },
  ],
});

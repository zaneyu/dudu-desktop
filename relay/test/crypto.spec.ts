import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { describe, expect, it } from "vitest";

import { createRecipientForTest, decryptPayloadForTest, encryptPayload } from "../sender-src/crypto.js";
import type { RemoteMessagePayloadV1 } from "../src/protocol/types.js";

function payload(text: string): RemoteMessagePayloadV1 {
  return { kind: "note", text, reaction: "none" };
}

function metadata(messageId: string): { messageId: string } {
  return { messageId };
}

describe("encryptPayload / decryptPayloadForTest", () => {
  it("uses fresh ephemeral keys and decrypts both envelopes", async () => {
    const recipient = await createRecipientForTest();
    const first = await encryptPayload(
      recipient.publicKey,
      payload("hello"),
      metadata("11111111-1111-4111-8111-111111111111"));
    const second = await encryptPayload(
      recipient.publicKey,
      payload("hello"),
      metadata("22222222-2222-4222-8222-222222222222"));

    expect(first.ephemeralPublicKey).not.toEqual(second.ephemeralPublicKey);
    await expect(decryptPayloadForTest(recipient.privateKey, first)).resolves.toEqual(payload("hello"));
    await expect(decryptPayloadForTest(recipient.privateKey, second)).resolves.toEqual(payload("hello"));
  });
});

interface ProcessResult {
  exitCode: number;
  stdout: string;
  stderr: string;
}

function runProcess(command: string, args: string[]): Promise<ProcessResult> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk: Buffer) => {
      stdout += chunk.toString("utf8");
    });
    child.stderr.on("data", (chunk: Buffer) => {
      stderr += chunk.toString("utf8");
    });
    child.on("error", reject);
    child.on("close", (exitCode) => resolve({ exitCode: exitCode ?? -1, stdout, stderr }));
  });
}

describe("TypeScript -> C# interop", () => {
  it("produces an envelope that Dudu.CryptoInterop can decrypt", async () => {
    const dotnetCommand = process.env.DUDU_DOTNET ?? "dotnet";
    const dllPath = process.env.DUDU_CRYPTO_INTEROP
      ?? path.resolve(
        import.meta.dirname,
        "..",
        "..",
        "tools",
        "Dudu.CryptoInterop",
        "bin",
        "Release",
        "net10.0-windows10.0.26100.0",
        "Dudu.CryptoInterop.dll");

    if (!existsSync(dllPath)) {
      throw new Error(
        `Dudu.CryptoInterop.dll not found at ${dllPath}. Build it first: `
          + "dotnet build tools/Dudu.CryptoInterop/Dudu.CryptoInterop.csproj -c Release");
    }

    const recipient = await createRecipientForTest();
    const text = "hello from vitest";
    const envelope = await encryptPayload(
      recipient.publicKey,
      payload(text),
      metadata("33333333-3333-4333-8333-333333333333"));

    const privateKeyPkcs8 = await crypto.subtle.exportKey("pkcs8", recipient.privateKey);

    const tempDir = await mkdtemp(path.join(tmpdir(), "dudu-crypto-interop-"));
    try {
      const privateKeyPath = path.join(tempDir, "private.pkcs8");
      const envelopePath = path.join(tempDir, "envelope.json");
      await writeFile(privateKeyPath, Buffer.from(privateKeyPkcs8));
      await writeFile(envelopePath, JSON.stringify(envelope), "utf8");

      const result = await runProcess(
        dotnetCommand,
        [dllPath, "decrypt", "--private", privateKeyPath, "--envelope", envelopePath]);

      expect(result.exitCode, `CryptoInterop failed: ${result.stderr}`).toBe(0);
      expect(JSON.parse(result.stdout)).toEqual(payload(text));
    } finally {
      await rm(tempDir, { recursive: true, force: true });
    }
  });
});

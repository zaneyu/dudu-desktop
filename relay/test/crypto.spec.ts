import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { describe, expect, it } from "vitest";

import {
  createRecipientForTest,
  decryptPayloadForTest,
  encryptPayload,
  EnvelopeDecryptionError,
  EnvelopeValidationError,
  validateEnvelopeShape,
} from "../sender-src/crypto.js";
import type { EncryptedEnvelopeV1, RemoteMessagePayloadV1 } from "../src/protocol/types.js";

function payload(text: string): RemoteMessagePayloadV1 {
  return { kind: "note", text, reaction: "none" };
}

function metadata(messageId: string): { messageId: string } {
  return { messageId };
}

/** Test-only Base64URL helpers. Node's Buffer is fine here: only sender-src/crypto.ts itself
 * must stay runtime-neutral. */
function base64UrlEncode(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64url");
}

function base64UrlDecode(value: string): Uint8Array {
  return new Uint8Array(Buffer.from(value, "base64url"));
}

function flipLastBit(base64Url: string): string {
  const bytes = base64UrlDecode(base64Url);
  bytes[bytes.length - 1] ^= 0x01;
  return base64UrlEncode(bytes);
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

describe("decryptPayloadForTest rejects tampered envelopes", () => {
  it("rejects a modified authentication tag", async () => {
    const recipient = await createRecipientForTest();
    const secret = "private hello";
    const envelope = await encryptPayload(recipient.publicKey, payload(secret), metadata("11111111-1111-4111-8111-111111111111"));
    const tampered: EncryptedEnvelopeV1 = { ...envelope, ciphertext: flipLastBit(envelope.ciphertext) };

    const error = await decryptPayloadForTest(recipient.privateKey, tampered).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(EnvelopeDecryptionError);
    expect((error as Error).message).not.toContain(secret);
  });

  it("rejects a modified messageId because it changes the authenticated data", async () => {
    const recipient = await createRecipientForTest();
    const secret = "private hello";
    const envelope = await encryptPayload(recipient.publicKey, payload(secret), metadata("11111111-1111-4111-8111-111111111111"));
    const tampered: EncryptedEnvelopeV1 = { ...envelope, messageId: "22222222-2222-4222-8222-222222222222" };

    const error = await decryptPayloadForTest(recipient.privateKey, tampered).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(EnvelopeDecryptionError);
    expect((error as Error).message).not.toContain(secret);
  });

  it("rejects a modified nonce", async () => {
    const recipient = await createRecipientForTest();
    const secret = "private hello";
    const envelope = await encryptPayload(recipient.publicKey, payload(secret), metadata("11111111-1111-4111-8111-111111111111"));
    const tampered: EncryptedEnvelopeV1 = { ...envelope, nonce: flipLastBit(envelope.nonce) };

    const error = await decryptPayloadForTest(recipient.privateKey, tampered).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(EnvelopeDecryptionError);
    expect((error as Error).message).not.toContain(secret);
  });

  it("rejects a decrypted payload over 4096 UTF-8 bytes", async () => {
    const recipient = await createRecipientForTest();
    // 1,100 four-byte UTF-8 scalar values (1,100 < 2,000) push the JSON payload itself over the
    // 4,096 byte ceiling without tripping the separate text-length rule.
    const text = "\u{1F642}".repeat(1100);
    const envelope = await encryptPayload(recipient.publicKey, payload(text), metadata("11111111-1111-4111-8111-111111111111"));

    const error = await decryptPayloadForTest(recipient.privateKey, envelope).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(EnvelopeValidationError);
    expect((error as Error).message).not.toContain(text);
  });

  it("rejects text over 2000 Unicode scalar values", async () => {
    const recipient = await createRecipientForTest();
    const text = "a".repeat(2001);
    const envelope = await encryptPayload(recipient.publicKey, payload(text), metadata("11111111-1111-4111-8111-111111111111"));

    const error = await decryptPayloadForTest(recipient.privateKey, envelope).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(EnvelopeValidationError);
    expect((error as Error).message).not.toContain(text);
  });

  it("rejects an unknown reaction", async () => {
    const recipient = await createRecipientForTest();
    const rawPayload = { kind: "note", text: "hi", reaction: "party" } as unknown as RemoteMessagePayloadV1;
    const envelope = await encryptPayload(recipient.publicKey, rawPayload, metadata("11111111-1111-4111-8111-111111111111"));

    const error = await decryptPayloadForTest(recipient.privateKey, envelope).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(EnvelopeValidationError);
    expect((error as Error).message).not.toContain("hi");
  });

  it("rejects unknown payload keys", async () => {
    const recipient = await createRecipientForTest();
    const rawPayload = { kind: "note", text: "hi", reaction: "none", extra: "nope" } as unknown as RemoteMessagePayloadV1;
    const envelope = await encryptPayload(recipient.publicKey, rawPayload, metadata("11111111-1111-4111-8111-111111111111"));

    const error = await decryptPayloadForTest(recipient.privateKey, envelope).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(EnvelopeValidationError);
    expect((error as Error).message).not.toContain("hi");
  });
});

describe("validateEnvelopeShape rejects malformed envelopes", () => {
  async function validEnvelope(): Promise<EncryptedEnvelopeV1> {
    const recipient = await createRecipientForTest();
    return encryptPayload(recipient.publicKey, payload("hello"), metadata("11111111-1111-4111-8111-111111111111"));
  }

  it("rejects a non-v1 protocolVersion", async () => {
    const envelope = { ...(await validEnvelope()), protocolVersion: 2 } as unknown as EncryptedEnvelopeV1;
    await expect(validateEnvelopeShape(envelope)).rejects.toBeInstanceOf(EnvelopeValidationError);
  });

  it("rejects an invalid messageId format (uppercase UUID)", async () => {
    const envelope = { ...(await validEnvelope()), messageId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa".toUpperCase() };
    await expect(validateEnvelopeShape(envelope)).rejects.toBeInstanceOf(EnvelopeValidationError);
  });

  it("rejects a createdUtc more than 5 minutes in the future", async () => {
    const future = new Date(Date.now() + 10 * 60_000).toISOString();
    const envelope = { ...(await validEnvelope()), createdUtc: future };
    await expect(validateEnvelopeShape(envelope)).rejects.toBeInstanceOf(EnvelopeValidationError);
  });

  it("rejects an unknown envelope key", async () => {
    const envelope = { ...(await validEnvelope()), extra: "nope" };
    await expect(validateEnvelopeShape(envelope)).rejects.toBeInstanceOf(EnvelopeValidationError);
  });

  it("rejects an ephemeralPublicKey that is not a valid P-256 SPKI key", async () => {
    const garbage = base64UrlEncode(new Uint8Array(91).fill(7));
    const envelope = { ...(await validEnvelope()), ephemeralPublicKey: garbage };
    await expect(validateEnvelopeShape(envelope)).rejects.toBeInstanceOf(EnvelopeValidationError);
  });

  it("rejects an hkdfSalt of the wrong length", async () => {
    const envelope = { ...(await validEnvelope()), hkdfSalt: base64UrlEncode(new Uint8Array(16)) };
    await expect(validateEnvelopeShape(envelope)).rejects.toBeInstanceOf(EnvelopeValidationError);
  });

  it("rejects a nonce of the wrong length", async () => {
    const envelope = { ...(await validEnvelope()), nonce: base64UrlEncode(new Uint8Array(8)) };
    await expect(validateEnvelopeShape(envelope)).rejects.toBeInstanceOf(EnvelopeValidationError);
  });

  it("rejects an undersized ciphertext", async () => {
    const envelope = { ...(await validEnvelope()), ciphertext: base64UrlEncode(new Uint8Array(10)) };
    await expect(validateEnvelopeShape(envelope)).rejects.toBeInstanceOf(EnvelopeValidationError);
  });

  it("rejects an oversized ciphertext", async () => {
    const envelope = { ...(await validEnvelope()), ciphertext: base64UrlEncode(new Uint8Array(6145)) };
    await expect(validateEnvelopeShape(envelope)).rejects.toBeInstanceOf(EnvelopeValidationError);
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

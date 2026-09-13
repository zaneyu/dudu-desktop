/**
 * Node CLI fixture used to prove the C#-can-decrypt-TypeScript direction of interop: it encrypts
 * a note with the exact same sender-src/crypto.ts implementation the browser sender will use, and
 * prints the resulting envelope as JSON to stdout for a C# test to decrypt.
 *
 * Usage:
 *   node relay/dist/src/protocol/encrypt-fixture.js --public <spki-base64url> --text <text>
 *     [--reaction r] [--message-id id] [--deliver-after iso]
 */

import { encryptPayload, importRecipientPublicKey } from "../../sender-src/crypto.js";
import type { RemoteMessagePayloadV1 } from "./types.js";

function readArg(flag: string): string | undefined {
  const index = process.argv.indexOf(flag);
  if (index === -1 || index === process.argv.length - 1) {
    return undefined;
  }
  return process.argv[index + 1];
}

function requireArg(flag: string): string {
  const value = readArg(flag);
  if (value === undefined) {
    throw new Error(`Missing required argument: ${flag}`);
  }
  return value;
}

async function main(): Promise<void> {
  const publicKeyBase64Url = requireArg("--public");
  const text = requireArg("--text");
  const reaction = (readArg("--reaction") ?? "none") as RemoteMessagePayloadV1["reaction"];
  const messageId = readArg("--message-id") ?? crypto.randomUUID();
  const deliverAfterUtc = readArg("--deliver-after") ?? null;

  const publicKey = await importRecipientPublicKey(publicKeyBase64Url);
  const payload: RemoteMessagePayloadV1 = { kind: "note", text, reaction };

  const envelope = await encryptPayload(publicKey, payload, { messageId, deliverAfterUtc });

  process.stdout.write(JSON.stringify(envelope));
}

main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

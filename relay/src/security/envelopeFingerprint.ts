import type { EncryptedEnvelopeV1 } from "../protocol/types.js";
import { sha256HexOfText } from "./tokens.js";

/**
 * Hashes only the wire envelope, never decrypted note content. Property order is explicit so the
 * same envelope hashes identically in every request and the value remains safe to retain as an
 * idempotency witness.
 */
export function envelopeFingerprintInput(envelope: EncryptedEnvelopeV1): string {
  return JSON.stringify({
    protocolVersion: envelope.protocolVersion,
    messageId: envelope.messageId,
    createdUtc: envelope.createdUtc,
    deliverAfterUtc: envelope.deliverAfterUtc,
    ephemeralPublicKey: envelope.ephemeralPublicKey,
    hkdfSalt: envelope.hkdfSalt,
    nonce: envelope.nonce,
    ciphertext: envelope.ciphertext,
  });
}

export async function envelopeFingerprint(envelope: EncryptedEnvelopeV1): Promise<string> {
  return sha256HexOfText(envelopeFingerprintInput(envelope));
}

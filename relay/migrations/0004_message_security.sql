-- Preserve message ownership beyond the short sender-visible status retention window. The hash is
-- over the complete encrypted envelope (never plaintext), so retries with a changed ciphertext or
-- changed authenticated metadata cannot silently reuse an existing message id.
CREATE TABLE message_ownership (
  id TEXT PRIMARY KEY,
  sender_session_id TEXT NOT NULL,
  device_id TEXT NOT NULL,
  envelope_hash TEXT,
  state TEXT NOT NULL CHECK (state IN ('queued', 'delivered')),
  created_utc TEXT NOT NULL,
  expires_utc TEXT NOT NULL
);

CREATE INDEX idx_message_ownership_expires ON message_ownership (expires_utc);
CREATE INDEX idx_message_ownership_device ON message_ownership (device_id);

-- Backfill ownership that can still be reconstructed. Legacy rows have no envelope hash, so the
-- application treats them conservatively: they remain claimed, but a retry is not accepted as an
-- idempotent match unless the complete legacy envelope can be resolved by the caller.
INSERT INTO message_ownership (id, sender_session_id, device_id, envelope_hash, state, created_utc, expires_utc)
SELECT s.id, s.sender_session_id, s.device_id, NULL, s.state, s.updated_utc,
       COALESCE(m.expires_utc, s.expires_utc)
FROM message_status s
LEFT JOIN messages m ON m.id = s.id AND m.device_id = s.device_id
ON CONFLICT (id) DO NOTHING;

-- A poll claim is deliberately separate from delivery state. It prevents concurrent polls from
-- returning the same ciphertext while retaining at-least-once recovery if the desktop crashes
-- before it can acknowledge the message.
ALTER TABLE messages ADD COLUMN delivery_claim_token TEXT;
ALTER TABLE messages ADD COLUMN delivery_claim_expires_utc TEXT;

CREATE INDEX idx_messages_delivery_claim
  ON messages (device_id, delivery_claim_expires_utc);

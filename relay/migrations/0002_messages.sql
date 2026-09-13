-- Encrypted message queue: ciphertext plus routing metadata only, no plaintext and no read
-- receipts. `messages` holds the queued ciphertext (deleted on acknowledgment or expiry).
-- `message_status` tracks delivery state independently of `messages`, so a sender can observe
-- "delivered" after the ciphertext row is gone, and so status itself can be pruned on its own
-- (shorter) 24-hour clock without touching `messages`.

CREATE TABLE messages (
  id TEXT NOT NULL,
  device_id TEXT NOT NULL REFERENCES devices (id),
  protocol_version INTEGER NOT NULL,
  created_utc TEXT NOT NULL,
  deliver_after_utc TEXT,
  expires_utc TEXT NOT NULL,
  ephemeral_public_key TEXT NOT NULL,
  hkdf_salt TEXT NOT NULL,
  nonce TEXT NOT NULL,
  ciphertext TEXT NOT NULL,
  PRIMARY KEY (device_id, id)
);

CREATE INDEX idx_messages_device_deliver ON messages (device_id, deliver_after_utc);

CREATE TABLE message_status (
  id TEXT PRIMARY KEY,
  sender_session_id TEXT NOT NULL,
  device_id TEXT NOT NULL,
  state TEXT NOT NULL,
  updated_utc TEXT NOT NULL,
  expires_utc TEXT NOT NULL
);

CREATE INDEX idx_message_status_device ON message_status (device_id);

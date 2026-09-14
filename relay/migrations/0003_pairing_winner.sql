-- The session id that won redemption is a per-request marker. A timestamp is not sufficient:
-- two concurrent requests can receive the same millisecond timestamp.
ALTER TABLE pairing_codes ADD COLUMN consumed_session_id TEXT;
